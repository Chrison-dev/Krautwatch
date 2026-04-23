using MediathekNext.Domain.Enums;

namespace MediathekNext.Application.Downloads;

// ── Wolverine message published by API, handled by Worker ─────
public record StartDownloadCommand(
    Guid JobId,
    string EpisodeId,
    VideoQuality Quality,
    string StreamUrl,
    string OutputDirectory,
    TimeSpan? EpisodeDuration = null);

// ── Request DTOs ──────────────────────────────────────────────
public record CancelDownloadRequest(Guid JobId);
public record RetryDownloadRequest(Guid JobId);

// ── Response DTO ──────────────────────────────────────────────
public record DownloadJobResponse(
    Guid JobId,
    string EpisodeId,
    string EpisodeTitle,
    string ShowTitle,
    string ChannelName,
    string Quality,
    string Status,           // matches DownloadStatus enum name
    string? StreamType,      // "HLS" | "MP4" | null (before resolve)
    double? ProgressPercent,
    string? ErrorMessage,
    string? OutputPath,
    long? FileSizeBytes,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);
