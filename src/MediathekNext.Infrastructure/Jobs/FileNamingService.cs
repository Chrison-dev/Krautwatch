using MediathekNext.Domain.Entities;
using MediathekNext.Domain.Enums;

namespace MediathekNext.Infrastructure.Jobs;

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

    public string BuildFinalPath(
        string downloadDirectory,
        Episode episode,
        VideoQuality quality,
        string extension = "mp4")
    {
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
