using MediathekNext.Domain.Enums;

namespace MediathekNext.Domain.Entities;

public class DownloadJob
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string EpisodeId { get; init; } = default!;
    public Episode Episode { get; init; } = default!;
    public string StreamUrl { get; init; } = default!;
    public VideoQuality Quality { get; init; }
    public DownloadStatus Status { get; private set; } = DownloadStatus.Queued;
    public double? ProgressPercent { get; private set; }
    public string? ErrorMessage { get; private set; }
    public string? OutputPath { get; private set; }
    public long? FileSizeBytes { get; private set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; private set; }

    public void MarkDownloading() => Status = DownloadStatus.Downloading;

    public void UpdateProgress(double percent) =>
        ProgressPercent = Math.Clamp(percent, 0, 100);

    public void MarkCompleted(string outputPath, long fileSizeBytes)
    {
        Status = DownloadStatus.Completed;
        OutputPath = outputPath;
        FileSizeBytes = fileSizeBytes;
        CompletedAt = DateTimeOffset.UtcNow;
    }

    public void MarkFailed(string errorMessage)
    {
        Status = DownloadStatus.Failed;
        ErrorMessage = errorMessage;
        CompletedAt = DateTimeOffset.UtcNow;
    }

    public void MarkCancelled()
    {
        Status = DownloadStatus.Cancelled;
        CompletedAt = DateTimeOffset.UtcNow;
    }
}
