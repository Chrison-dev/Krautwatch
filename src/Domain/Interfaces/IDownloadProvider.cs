using Krautwatch.Domain.Entities;

namespace Krautwatch.Domain.Interfaces;

/// <summary>The outcome of a completed download: where the file landed and how big it is.</summary>
public record DownloadResult(string OutputPath, long SizeBytes);

/// <summary>
/// The download engine port. The Downloader agent's adapter pulls the job's stream to disk and
/// reports coarse progress; today that's a raw progressive-MP4 copy (no transcoding), with HLS
/// remux / ffmpeg deferred to a later orchestration step (DR-010).
/// </summary>
public interface IDownloadProvider
{
    Task<DownloadResult> DownloadAsync(
        DownloadJob job,
        string outputDirectory,
        IProgress<double> progress,
        CancellationToken ct = default);
}
