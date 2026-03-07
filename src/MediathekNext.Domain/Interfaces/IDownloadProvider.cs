using MediathekNext.Domain.Entities;

namespace MediathekNext.Domain.Interfaces;

/// <summary>
/// Abstraction for the download engine (ffmpeg, etc.)
/// </summary>
public interface IDownloadProvider
{
    Task DownloadAsync(
        DownloadJob job,
        string outputDirectory,
        IProgress<double> progress,
        CancellationToken ct = default);
}
