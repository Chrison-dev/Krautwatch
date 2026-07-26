using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Interfaces;

namespace Krautwatch.Infrastructure.Downloads;

/// <summary>
/// The <see cref="IDownloadProvider"/> the Downloader agent actually resolves — it routes each job to
/// the right engine by its stream: HLS (<c>.m3u8</c>) is remuxed with ffmpeg, everything else (a
/// progressive MP4, incl. ZDF's extension-less URLs) is a raw byte copy. Keeps the engine choice an
/// Infrastructure detail; the Application Action just calls the port.
/// </summary>
public sealed class DownloadDispatcher(RawMp4DownloadProvider raw, FfmpegDownloadProvider ffmpeg) : IDownloadProvider
{
    public Task<DownloadResult> DownloadAsync(
        DownloadJob job, string outputDirectory, IProgress<double> progress, CancellationToken ct = default) =>
        IsHls(job.StreamUrl)
            ? ffmpeg.DownloadAsync(job, outputDirectory, progress, ct)
            : raw.DownloadAsync(job, outputDirectory, progress, ct);

    internal static bool IsHls(string streamUrl) =>
        streamUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);
}
