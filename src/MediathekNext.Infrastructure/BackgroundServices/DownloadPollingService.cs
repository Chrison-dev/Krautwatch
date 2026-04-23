using MediathekNext.Domain.Entities;
using MediathekNext.Domain.Enums;
using MediathekNext.Domain.Interfaces;
using MediathekNext.Infrastructure.Downloads;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MediathekNext.Infrastructure.BackgroundServices;

/// <summary>
/// Polls DownloadJobs for Queued entries and executes them in parallel via ffmpeg.
///
/// Concurrency model:
///   - A SemaphoreSlim gates how many downloads run simultaneously (MaxConcurrent).
///   - The polling loop fires a new Task for each claimed job without awaiting it,
///     so up to MaxConcurrent downloads run in parallel within this worker process.
///   - Across multiple worker containers, EF Core's [ConcurrencyCheck] on RowVersion
///     prevents two workers from claiming the same job — no broker required.
///
/// MaxConcurrent is read from config key Downloads:MaxConcurrent (default 2).
/// </summary>
public class DownloadPollingService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<DownloadPollingService> logger) : BackgroundService
{
    private readonly string _workerId =
        $"worker-{Environment.MachineName}-{Guid.NewGuid():N}"[..32];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var maxConcurrent = int.TryParse(configuration["Downloads:MaxConcurrent"], out var parsed) ? parsed : 2;
        maxConcurrent = Math.Max(1, Math.Min(maxConcurrent, 16)); // clamp 1–16

        // Semaphore gates parallel download slots
        using var semaphore = new SemaphoreSlim(maxConcurrent, maxConcurrent);
        // Tracks in-flight download tasks so we can await them on shutdown
        var inFlight = new HashSet<Task>();
        var inFlightLock = new object();

        logger.LogInformation(
            "Download polling started. WorkerId={WorkerId} MaxConcurrent={Max}",
            _workerId, maxConcurrent);

        await RequeueInterruptedJobsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Only poll if there's a free slot — avoids claiming a job we can't start yet
                await semaphore.WaitAsync(stoppingToken);

                var (job, outputDir) = await TryClaimNextJobAsync(stoppingToken);

                if (job is null)
                {
                    // Nothing queued — release the slot and back off
                    semaphore.Release();
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    continue;
                }

                // Capture locals for the closure
                var capturedJob = job;
                var capturedDir = outputDir!;

                // Fire the download on a thread-pool thread — don't await here.
                // The semaphore slot is released in the finally block so the polling
                // loop can claim the next job as soon as a slot opens up.
                Task downloadTask = null!;
                downloadTask = Task.Run(async () =>
                {
                    try
                    {
                        await ExecuteJobAsync(capturedJob, capturedDir, stoppingToken);
                    }
                    finally
                    {
                        semaphore.Release();
                        lock (inFlightLock) inFlight.Remove(downloadTask);
                    }
                }, CancellationToken.None); // None: let ExecuteJobAsync handle stoppingToken

                lock (inFlightLock) inFlight.Add(downloadTask);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled error in polling loop");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }

        // Graceful shutdown — wait for all in-flight downloads to finish
        Task[] pending;
        lock (inFlightLock) pending = [.. inFlight];
        if (pending.Length > 0)
        {
            logger.LogInformation(
                "Shutdown: waiting for {Count} in-flight download(s) to complete", pending.Length);
            await Task.WhenAll(pending);
        }

        logger.LogInformation("Download polling stopped. WorkerId={WorkerId}", _workerId);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Claim: SELECT next Queued job, attempt optimistic UPDATE
    // ──────────────────────────────────────────────────────────────────────

    private async Task<(DownloadJob? Job, string? OutputDir)> TryClaimNextJobAsync(
        CancellationToken ct)
    {
        using var scope  = scopeFactory.CreateScope();
        var jobs         = scope.ServiceProvider.GetRequiredService<IDownloadJobRepository>();
        var settings     = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();

        var job = await jobs.GetNextQueuedAsync(ct);
        if (job is null) return (null, null);

        job.MarkClaiming(_workerId);
        try
        {
            await jobs.UpdateAsync(job, ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another worker or thread claimed it first — expected under parallel load
            logger.LogDebug("Job {JobId} already claimed — skipping", job.Id);
            return (null, null);
        }

        var appSettings = await settings.GetAsync(ct);
        return (job, appSettings.DownloadDirectory);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Execute: run ffmpeg, report progress, mark final state
    // ──────────────────────────────────────────────────────────────────────

    private async Task ExecuteJobAsync(
        DownloadJob job,
        string outputDirectory,
        CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Executing download: Job={JobId} Episode={EpisodeId} Quality={Quality}",
            job.Id, job.EpisodeId, job.Quality);

        using var scope  = scopeFactory.CreateScope();
        var jobs         = scope.ServiceProvider.GetRequiredService<IDownloadJobRepository>();
        var downloader   = scope.ServiceProvider.GetRequiredService<IFfmpegDownloader>();

        try
        {
            var result = await downloader.DownloadAsync(
                jobId:           job.Id,
                url:             job.StreamUrl,
                quality:         job.Quality,
                outputDirectory: outputDirectory,
                onProgress: async pct =>
                {
                    job.UpdateProgress(pct);
                    try
                    {
                        await jobs.UpdateAsync(job, stoppingToken);
                    }
                    catch (DbUpdateConcurrencyException)
                    {
                        var current = await jobs.GetByIdAsync(job.Id, stoppingToken);
                        if (current?.Status == DownloadStatus.Cancelled)
                            throw new OperationCanceledException("Job cancelled externally");
                    }
                },
                knownDuration: job.Episode?.Duration is { } d && d > TimeSpan.Zero ? d : null,
                ct: stoppingToken);

            job.MarkCompleted(result.OutputPath, result.FileSizeBytes);
            await jobs.UpdateAsync(job, CancellationToken.None);

            logger.LogInformation(
                "Download completed: Job={JobId} File={Path} Size={Bytes}b",
                job.Id, result.OutputPath, result.FileSizeBytes);
        }
        catch (OperationCanceledException)
        {
            job.MarkCancelled();
            await TryPersist(jobs, job);
            logger.LogInformation("Download cancelled: Job={JobId}", job.Id);
        }
        catch (Exception ex)
        {
            job.MarkFailed(ex.Message);
            await TryPersist(jobs, job);
            logger.LogError(ex, "Download failed: Job={JobId}", job.Id);
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // Startup: mark jobs from a previous crashed run as failed
    // ──────────────────────────────────────────────────────────────────────

    private async Task RequeueInterruptedJobsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IDownloadJobRepository>();

        var interrupted = await jobs.GetByWorkerIdAsync(_workerId, ct);
        if (interrupted.Count == 0) return;

        logger.LogWarning(
            "Found {Count} interrupted job(s) from previous run — marking failed",
            interrupted.Count);

        foreach (var job in interrupted)
        {
            job.MarkFailed("Worker restarted mid-download — please retry");
            await TryPersist(jobs, job);
        }
    }

    private static async Task TryPersist(IDownloadJobRepository jobs, DownloadJob job)
    {
        try { await jobs.UpdateAsync(job, CancellationToken.None); }
        catch { /* best-effort on shutdown path */ }
    }
}
