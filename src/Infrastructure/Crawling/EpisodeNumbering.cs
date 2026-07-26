using System.Text.RegularExpressions;

namespace Krautwatch.Infrastructure.Crawling;

/// <summary>
/// Extracts a (season, episode) pair from a broadcaster's episode title so the catalog can carry
/// Sonarr-style numbering. German public-TV titles express it a few ways — "S02E52", "(S02/E52)",
/// "Staffel 2, Folge 52", "2x52". When nothing matches, the episode stays air-date-only (Daily).
/// </summary>
public static partial class EpisodeNumbering
{
    // S02E52 / S02 E52 / S02/E52 / s2e5
    [GeneratedRegex(@"[Ss](?<s>\d{1,3})\s*[/xXeE\.\- ]?\s*[Ee](?<e>\d{1,4})", RegexOptions.CultureInvariant)]
    private static partial Regex SeasonEpisode();

    // Staffel 2 … Folge 52  (German)
    [GeneratedRegex(@"Staffel\s*(?<s>\d{1,3}).*?Folge\s*(?<e>\d{1,4})",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex StaffelFolge();

    // 2x52  (season x episode) — require the season side to be short so a year like "2026x…" won't hit
    [GeneratedRegex(@"(?<![\dxX])(?<s>\d{1,2})[xX](?<e>\d{1,4})(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex SeasonXEpisode();

    /// <summary>Returns (season, episode) if the title encodes it, else (null, null).</summary>
    public static (int? Season, int? Episode) Parse(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return (null, null);

        foreach (var pattern in new[] { SeasonEpisode(), StaffelFolge(), SeasonXEpisode() })
        {
            var m = pattern.Match(title);
            if (m.Success
                && int.TryParse(m.Groups["s"].Value, out var s)
                && int.TryParse(m.Groups["e"].Value, out var e))
                return (s, e);
        }
        return (null, null);
    }
}
