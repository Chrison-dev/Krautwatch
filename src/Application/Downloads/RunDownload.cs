using Krautwatch.Domain.Entities;
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
    ILogger<RunDownloadHandler> logger)
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

        try
        {
            var download = provider.DownloadAsync(job, directory, progress, ct);
            while (!download.IsCompleted)
            {
                var finished = await Task.WhenAny(download, Task.Delay(ProgressInterval, ct));
                if (finished != download)
                {
                    job.UpdateProgress(latest);
                    await jobs.UpdateAsync(job, ct);
                }
            }

            var result = await download;
            job.MarkCompleted(result.OutputPath, result.SizeBytes);
            logger.LogInformation("Download {JobId} completed: {Path}", job.Id, result.OutputPath);
        }
        catch (OperationCanceledException)
        {
            throw; // shutdown — leave the job Downloading; startup reclaim requeues it
        }
        catch (Exception ex)
        {
            job.MarkDownloadFailed(ex.Message);
            logger.LogError(ex, "Download {JobId} failed", job.Id);
        }

        await jobs.UpdateAsync(job, ct);
    }
}
