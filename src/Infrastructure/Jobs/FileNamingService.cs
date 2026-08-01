using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Enums;

namespace Krautwatch.Infrastructure.Jobs;

/// <summary>
/// Converts episode metadata into a structured output path:
///   {baseDir}/{Channel}/{Show}/{Show} - {Title} ({Date}).{ext}
///
/// Sanitises all path components to be safe on Linux and Windows.
/// </summary>
public class FileNamingService
{
    private static readonly char[] InvalidChars =
        Path.GetInvalidFileNameChars()
            .Concat([':', '?', '*', '"', '<', '>', '|'])
            .Distinct()
            .ToArray();

    /// <summary>
    /// Where a finished download lands.
    /// </summary>
    /// <param name="releaseName">
    /// The release title an <c>*arr</c> app grabbed this as, or null for a download started from our own UI.
    /// </param>
    /// <remarks>
    /// <para>
    /// Two layouts, because there are two consumers. A download an <c>*arr</c> app grabbed goes to
    /// <c>{dir}/{release}/{release}.{ext}</c> — the shape SABnzbd produces and the one Sonarr's importer
    /// expects, since it parses the name to work out the series and episode and then renames the file
    /// itself. The library layout below cannot be imported at all: "heute-show - heute-show vom 5. Juni
    /// 2026 (2026-06-05).mp4" contains no season or episode.
    /// </para>
    /// <para>
    /// Downloads started from our own UI keep the readable
    /// <c>{Channel}/{Show}/{Show} - {Title} ({Date})</c> layout — nothing is going to rename those, and a
    /// release-style filename would be a poor thing to hand a human browsing a folder.
    /// </para>
    /// </remarks>
    public string BuildFinalPath(
        string downloadDirectory,
        Episode episode,
        VideoQuality quality,
        string extension = "mp4",
        string? releaseName = null)
    {
        // Sanitise maps path separators to underscores, so a release name cannot escape the download
        // directory. It can still sanitise down to nothing — ".." trims to empty — and an empty component
        // would produce a bare dotfile at the root, so fall through to the library layout in that case.
        if (!string.IsNullOrWhiteSpace(releaseName) && Sanitise(releaseName) is { Length: > 0 } release)
            return Path.Combine(downloadDirectory, release, $"{release}.{extension}");

        var channel  = Sanitise(episode.Show?.Channel?.Name ?? "Unknown");
        var show     = Sanitise(episode.Show?.Title ?? "Unknown");
        var title    = Sanitise(episode.Title);
        var date     = episode.BroadcastDate.ToString("yyyy-MM-dd");
        var qualSuffix = quality switch
        {
            VideoQuality.High     => "",          // HD is default — no suffix clutter
            VideoQuality.Standard => " [SD]",
            VideoQuality.Low      => " [Mobile]",
            _                     => ""
        };
        var fileName = $"{show} - {title} ({date}){qualSuffix}.{extension}";
        return Path.Combine(downloadDirectory, channel, show, fileName);
    }

    public string BuildTempPath(string downloadDirectory, Guid jobId, VideoQuality quality)
    {
        var tmpDir = Path.Combine(downloadDirectory, ".tmp");
        Directory.CreateDirectory(tmpDir);
        return Path.Combine(tmpDir, $"{jobId}-{quality}.mp4.part");
    }

    private static string Sanitise(string input)
    {
        var clean = string.Concat(input.Select(c =>
            InvalidChars.Contains(c) ? '_' : c));

        // Collapse multiple underscores, trim whitespace/dots
        return clean.Trim().TrimEnd('.');
    }
}
