using MediathekNext.Application.Downloads;
using MediathekNext.Domain.Interfaces;
using MediathekNext.Infrastructure.Downloads;
using Microsoft.Extensions.Logging;

namespace MediathekNext.Worker.Handlers;

/// <summary>
/// Wolverine message handler — picked up automatically by convention (class ends in Handler,
/// method is Handle() and first arg is the message type).
/// Runs in the scalable Worker container (see DR-004).
/// </summary>
public class StartDownloadCommandHandler(
    IDownloadJobRepository jobRepository,
    IFfmpegDownloader downloader,
    ILogger<StartDownloadCommandHandler> logger)
{
    public async Task Handle(
        StartDownloadCommand command,
        CancellationToken cancellationToken)
    {
        var job = await jobRepository.GetByIdAsync(command.JobId, cancellationToken);

        if (job is null)
        {
            logger.LogWarning("Job {JobId} not found — may have been deleted. Skipping.", command.JobId);
            return;
        }

        // Check for pre-emptive cancellation (user cancelled while job was queued)
        if (job.Status == Domain.Enums.DownloadStatus.Cancelled)
        {
            logger.LogInformation("Job {JobId} was cancelled before processing started.", command.JobId);
            return;
        }

        logger.LogInformation(
            "Starting download: Job={JobId} Episode={EpisodeId} Quality={Quality}",
            command.JobId, command.EpisodeId, command.Quality);

        job.MarkDownloading();
        await jobRepository.UpdateAsync(job, cancellationToken);

        try
        {
            var result = await downloader.DownloadAsync(
                jobId:            command.JobId,
                url:              command.StreamUrl,
                quality:          command.Quality,
                outputDirectory:  command.OutputDirectory,
                knownDuration:    command.EpisodeDuration,
                onProgress:       async pct =>
                {
                    job.UpdateProgress(pct);
                    await jobRepository.UpdateAsync(job, cancellationToken);
                },
                ct: cancellationToken);

            job.MarkCompleted(result.OutputPath, result.FileSizeBytes);
            await jobRepository.UpdateAsync(job, cancellationToken);

            logger.LogInformation(
                "Download completed: Job={JobId} File={OutputPath} Size={FileSizeBytes}b",
                command.JobId, result.OutputPath, result.FileSizeBytes);
        }
        catch (OperationCanceledException)
        {
            job.MarkCancelled();
            await jobRepository.UpdateAsync(job, cancellationToken);
            logger.LogInformation("Download cancelled: Job={JobId}", command.JobId);
        }
        catch (Exception ex)
        {
            job.MarkFailed(ex.Message);
            await jobRepository.UpdateAsync(job, cancellationToken);
            logger.LogError(ex, "Download failed: Job={JobId}", command.JobId);
        }
    }
}
