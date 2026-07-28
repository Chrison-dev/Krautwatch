using System.Text.RegularExpressions;
using Krautwatch.Domain.Enums;

namespace Krautwatch.Application.Indexing;

/// <summary>Newznab category ids the *arr apps expect.</summary>
public static class NewznabCategory
{
    public const int Tv = 5000;
    public const int Movies = 2000;
}

/// <summary>
/// One Newznab "release" projected from a catalog <c>Episode</c>. The GUID/DownloadToken are the
/// stable <c>Episode.Id</c> (Sonarr dedups on the GUID; the token round-trips back via SABnzbd).
/// <c>Title</c>, <c>Size</c> and <c>Category</c> are computed so the mapper only copies raw fields.
/// </summary>
public record Release
{
    public required string Guid { get; init; }
    public required string DownloadToken { get; init; }
    public required string ShowTitle { get; init; }
    public required SeriesType SeriesType { get; init; }

    /// <summary>
    /// The show's TVDB id, when we have mapped one. Emitted as a <c>newznab:attr</c> so Sonarr can identify
    /// the series by id instead of parsing our title string — which is the difference between reliable
    /// matching and hoping a Mediathek title parses.
    /// </summary>
    public int? TvdbId { get; init; }
    public int? Season { get; init; }
    public int? Episode { get; init; }
    public DateTimeOffset PublishDate { get; init; }
    public TimeSpan Duration { get; init; }
    public ContentType ContentType { get; init; }

    // ≈4 Mbit/s — a rough size so Sonarr's quality/size checks have something sane to weigh.
    public long Size => (long)(Duration.TotalSeconds * 500_000);

    public int Category => ContentType == ContentType.Movie ? NewznabCategory.Movies : NewznabCategory.Tv;

    /// <summary>The release title Sonarr parses — SxxEyy for Standard, air-date for Daily.</summary>
    public string Title => ReleaseNaming.Build(ShowTitle, SeriesType, Season, Episode, PublishDate);
}

/// <summary>
/// Builds a release title in the shape Sonarr's parser expects for each <see cref="SeriesType"/>.
/// </summary>
public static partial class ReleaseNaming
{
    private const string QualityTag = "GERMAN.1080p.WEB.h264";

    /// <remarks>
    /// Having numbers decides the shape, <b>not</b> our own <see cref="SeriesType"/>. That field is a
    /// crawler guess and it guesses wrong: heute-show, extra 3 and ZDF Magazin Royale are all
    /// <see cref="SeriesType.Daily"/> to us but <c>standard</c> to TVDB and Sonarr, so keying off it emitted
    /// air-date titles that Sonarr rejected with "Unable to identify correct episode(s)". Numbers now only
    /// ever arrive from TVDB — the same source Sonarr matches against — so their presence is the signal.
    /// </remarks>
    public static string Build(string showTitle, SeriesType type, int? season, int? episode, DateTimeOffset publishDate)
    {
        var name = Slug(showTitle);
        return season is not null && episode is not null
            ? $"{name}.S{season.Value:D2}E{episode.Value:D2}.{QualityTag}"
            : $"{name}.{publishDate:yyyy-MM-dd}.{QualityTag}";
    }

    // Release-style token: collapse whitespace to dots, drop anything but word chars / dot / dash.
    private static string Slug(string title)
    {
        var dotted = WhitespaceRegex().Replace(title.Trim(), ".");
        return DisallowedRegex().Replace(dotted, "").Trim('.');
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[^\w.\-]")]
    private static partial Regex DisallowedRegex();
}
