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

/// <summary>
/// Fetches an episode's subtitle track and writes it beside the finished video (#20).
/// </summary>
/// <remarks>
/// Separate from <see cref="IDownloadProvider"/> on purpose: there are two video providers (raw MP4 and
/// the ffmpeg HLS remux) and the subtitle is the same sidecar fetch for both, so putting it on the video
/// port would duplicate it.
/// </remarks>
public interface ISubtitleFetcher
{
    /// <summary>
    /// Writes the subtitle beside <paramref name="videoPath"/> and returns where it landed, or null when
    /// there is nothing to fetch or the fetch failed. <b>Never throws</b> — a missing subtitle must not
    /// fail a video that downloaded perfectly well.
    /// </summary>
    Task<string?> FetchAsync(
        string subtitleUrl,
        string videoPath,
        bool geoRestricted,
        CancellationToken ct = default);
}
