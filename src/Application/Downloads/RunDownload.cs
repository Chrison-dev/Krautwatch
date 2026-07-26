using Krautwatch.Domain.Enums;
using Krautwatch.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace Krautwatch.Application.Downloads;

/// <summary>
/// The download <b>Action</b> (DR-009): IO-driven orchestration run by the Downloader agent. Claims a
/// queued job, pulls it via the <see cref="IDownloadProvider"/> port, and records the terminal state
/// (Completed with the output path/size, or DownloadFailed with the reason).
/// </summary>
public class RunDownloadHandler(
    IDownloadJobRepository jobs,
    IDownloadProvider provider,
    ISettingsRepository settings,
    ILogger<RunDownloadHandler> logger)
{
    private static readonly string WorkerId = $"downloader-{Environment.MachineName}";

    public async Task HandleAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = await jobs.GetByIdAsync(jobId, ct);
        if (job is null || job.Status != DownloadStatus.Queued) return; // already claimed / gone

        if (job.Episode is null)
        {
            job.MarkDownloadFailed("Episode metadata missing — cannot resolve an output path.");
            await jobs.UpdateAsync(job, ct);
            return;
        }

        // Claim first so a second poll can't pick the same job (status → Downloading).
        job.MarkClaiming(WorkerId);
        await jobs.UpdateAsync(job, ct);

        var directory = (await settings.GetAsync(ct)).DownloadDirectory;

        try
        {
            var result = await provider.DownloadAsync(job, directory, new Progress<double>(), ct);
            job.MarkCompleted(result.OutputPath, result.SizeBytes);
            logger.LogInformation("Download {JobId} completed: {Path}", job.Id, result.OutputPath);
        }
        catch (OperationCanceledException)
        {
            throw; // shutdown — leave the job Downloading for a future stale-job reclaim
        }
        catch (Exception ex)
        {
            job.MarkDownloadFailed(ex.Message);
            logger.LogError(ex, "Download {JobId} failed", job.Id);
        }

        await jobs.UpdateAsync(job, ct);
    }
}
