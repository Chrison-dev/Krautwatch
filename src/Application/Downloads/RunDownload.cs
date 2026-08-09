using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Enums;
using Krautwatch.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace Krautwatch.Application.Downloads;

/// <summary>
/// The download <b>Action</b> (DR-009): IO-driven orchestration run by the Downloader agent on a job
/// the repository has already atomically claimed (Downloading). It pulls the stream via the
/// <see cref="IDownloadProvider"/> port, persists progress periodically, and records the terminal
/// state (Completed with the output path/size, or DownloadFailed with the reason).
/// </summary>
public class RunDownloadHandler(
    IDownloadJobRepository jobs,
    IDownloadProvider provider,
    ISettingsRepository settings,
    ILogger<RunDownloadHandler> logger,
    ISubtitleFetcher? subtitles = null)
{
    // How often the download's progress is flushed to the DB (so Sonarr's queue shows a live %),
    // kept coarse to avoid a write per read.
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(5);

    public async Task HandleAsync(DownloadJob job, CancellationToken ct = default)
    {
        if (job.Episode is null)
        {
            job.MarkDownloadFailed("Episode metadata missing — cannot resolve an output path.");
            await jobs.UpdateAsync(job, ct);
            return;
        }

        var directory = (await settings.GetAsync(ct)).DownloadDirectory;

        // The provider reports on a threadpool thread; only stash the latest value here (no DB work).
        var latest = 0.0;
        var progress = new Progress<double>(p => latest = p);

        // Cancel is cross-process: the UI marks the job Cancelled in the DB; we poll for it and abort.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var cancelled = false;

        try
        {
            var download = provider.DownloadAsync(job, directory, progress, cts.Token);
            while (!download.IsCompleted)
            {
                var finished = await Task.WhenAny(download, Task.Delay(ProgressInterval, ct));
                if (finished == download) break;

                await jobs.UpdateProgressAsync(job.Id, latest, ct);
                if (await jobs.GetStatusAsync(job.Id, ct) == DownloadStatus.Cancelled)
                {
                    cancelled = true;
                    await cts.CancelAsync();
                }
            }

            var result = await download;

            // After the video, and deliberately not gating on it: a subtitle that 404s or times out must
            // not turn a perfectly good download into a failure (#20).
            await FetchSubtitlesAsync(job, result.OutputPath, ct);

            job.MarkCompleted(result.OutputPath, result.SizeBytes);
            await jobs.UpdateAsync(job, ct);
            logger.LogInformation("Download {JobId} completed: {Path}", job.Id, result.OutputPath);
        }
        catch (OperationCanceledException) when (cancelled)
        {
            // The UI already set Status=Cancelled and the provider cleaned up its partial file — leave it.
            logger.LogInformation("Download {JobId} cancelled.", job.Id);
        }
        catch (OperationCanceledException)
        {
            throw; // shutdown — leave the job Downloading; startup reclaim requeues it
        }
        catch (Exception ex)
        {
            job.MarkDownloadFailed(ex.Message);
            await jobs.UpdateAsync(job, ct);
            logger.LogError(ex, "Download {JobId} failed", job.Id);
        }
    }

    /// <summary>
    /// Fetches the episode's subtitle track beside the finished video, when the broadcaster published
    /// one. Best-effort throughout: no subtitle is a normal outcome for a lot of German public TV, and
    /// failing a completed download over one would be absurd.
    /// </summary>
    private async Task FetchSubtitlesAsync(DownloadJob job, string videoPath, CancellationToken ct)
    {
        var url = job.Episode?.SubtitleUrl;
        if (subtitles is null || string.IsNullOrWhiteSpace(url))
            return;

        var written = await subtitles.FetchAsync(url, videoPath, job.GeoRestricted, ct);
        if (written is null)
            logger.LogInformation("No subtitle written for {JobId}.", job.Id);
    }
}
