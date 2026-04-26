namespace MediathekNext.Crawlers.Core;

public enum StreamQuality { Sd, Normal, Hd, Uhd }

public enum StreamLanguage
{
    German, GermanAd, GermanDgs,
    English, French, Original, Other
}

public enum GeoRestriction { None, De, At, Ch, Dach, World }

/// <summary>
/// Broadcaster-agnostic intermediate model produced by all crawlers.
/// One CrawlResult = one watchable episode variant (language/accessibility).
/// </summary>
public sealed record CrawlResult(
    string BroadcasterKey,
    string ShowTitle,
    string? ShowExternalId,
    string EpisodeTitle,
    string? Description,
    DateTimeOffset? BroadcastTime,
    TimeSpan? Duration,
    string? WebsiteUrl,
    string? ThumbnailUrl,
    GeoRestriction Geo,
    string? EpisodeExternalId,
    IReadOnlyList<StreamEntry> Streams,
    IReadOnlyList<SubtitleEntry> Subtitles
);

public sealed record StreamEntry(
    StreamQuality Quality,
    StreamLanguage Language,
    string Url,
    bool IsHls = false
);

public sealed record SubtitleEntry(
    StreamLanguage Language,
    string Url
);
