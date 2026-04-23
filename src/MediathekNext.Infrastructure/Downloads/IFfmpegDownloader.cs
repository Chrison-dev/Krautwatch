using MediathekNext.Domain.Enums;

namespace MediathekNext.Infrastructure.Downloads;

public record DownloadResult(string OutputPath, long FileSizeBytes);

public interface IFfmpegDownloader
{
    /// <summary>
    /// Downloads a stream URL to disk using ffmpeg.
    /// Calls onProgress with a value between 0.0 and 100.0 periodically.
    /// Throws OperationCanceledException on cancellation.
    /// </summary>
    Task<DownloadResult> DownloadAsync(
        Guid jobId,
        string url,
        VideoQuality quality,
        string outputDirectory,
        Func<double, Task> onProgress,
        TimeSpan? knownDuration = null,
        CancellationToken ct = default);
}
