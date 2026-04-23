using MediathekNext.Domain.Enums;

namespace MediathekNext.Domain.Entities;

/// <summary>
/// Tracks the state and progress of a download across TickerQ job chain phases:
/// Resolve → Download → Finalise
///
/// TickerQ owns retry scheduling and distributed locking.
/// This entity owns domain state: progress, paths, error details, phase transitions.
/// </summary>
public class DownloadJob
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string EpisodeId { get; init; } = default!;
    public Episode? Episode { get; set; }
    public string StreamUrl { get; init; } = default!;
    public VideoQuality Quality { get; init; }
    public DownloadStatus Status { get; private set; } = DownloadStatus.Queued;

    /// <summary>Worker that claimed this job (set during MarkClaiming).</summary>
    public string? WorkerId { get; private set; }

    // ── Phase metadata (populated as the chain progresses) ────────────────

    /// <summary>Stream type detected in Resolve phase.</summary>
    public string? StreamType { get; private set; }   // "HLS" | "MP4"

    /// <summary>Content length from Resolve phase — improves progress accuracy.</summary>
    public long? ContentLengthBytes { get; private set; }

    /// <summary>Temp path written by Download phase, consumed by Finalise phase.</summary>
    public string? TempPath { get; private set; }

    // ── Progress & results ─────────────────────────────────────────────────

    public double? ProgressPercent { get; private set; }
    public string? ErrorMessage { get; private set; }
    public string? OutputPath { get; private set; }
    public long? FileSizeBytes { get; private set; }

    // ── Timestamps ────────────────────────────────────────────────────────

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    // ── Phase transitions ─────────────────────────────────────────────────

    public void MarkClaiming(string workerId)
    {
        WorkerId  = workerId;
        Status    = DownloadStatus.Downloading;
        StartedAt = DateTimeOffset.UtcNow;
    }

    public void MarkResolving()
    {
        Status    = DownloadStatus.Resolving;
        StartedAt = DateTimeOffset.UtcNow;
    }

    public void MarkResolved(string streamType, long? contentLengthBytes)
    {
        StreamType         = streamType;
        ContentLengthBytes = contentLengthBytes;
    }

    public void MarkDownloading()
        => Status = DownloadStatus.Downloading;

    public void UpdateProgress(double percent)
        => ProgressPercent = Math.Clamp(percent, 0, 100);

    public void MarkDownloaded(string tempPath)
    {
        TempPath = tempPath;
        ProgressPercent = 100;
    }

    public void MarkFinalising()
        => Status = DownloadStatus.Finalising;

    public void MarkCompleted(string outputPath, long fileSizeBytes)
    {
        Status       = DownloadStatus.Completed;
        OutputPath   = outputPath;
        FileSizeBytes = fileSizeBytes;
        CompletedAt  = DateTimeOffset.UtcNow;
    }

    // ── Terminal failures ─────────────────────────────────────────────────

    public void MarkUrlUnavailable(string reason)
    {
        Status       = DownloadStatus.UrlUnavailable;
        ErrorMessage = reason;
        CompletedAt  = DateTimeOffset.UtcNow;
    }

    public void MarkFailed(string reason) => MarkDownloadFailed(reason);

    public void MarkDownloadFailed(string reason)
    {
        Status       = DownloadStatus.DownloadFailed;
        ErrorMessage = reason;
        CompletedAt  = DateTimeOffset.UtcNow;
    }

    public void MarkFinaliseFailed(string reason)
    {
        Status       = DownloadStatus.FinaliseFailed;
        ErrorMessage = reason;
        CompletedAt  = DateTimeOffset.UtcNow;
    }

    public void MarkCancelled()
    {
        Status      = DownloadStatus.Cancelled;
        CompletedAt = DateTimeOffset.UtcNow;
    }

    // ── Convenience ───────────────────────────────────────────────────────

    public bool IsTerminal => Status is
        DownloadStatus.Completed or
        DownloadStatus.Cancelled or
        DownloadStatus.UrlUnavailable or
        DownloadStatus.DownloadFailed or
        DownloadStatus.FinaliseFailed;

    public bool IsActive => !IsTerminal;
}
