using MediathekNext.Crawlers.Core;

namespace MediathekNext.Crawlers.Zdf;

public interface IZdfClient
{
    /// <summary>Fetches one tab of the A-Z letter page, returning show refs and the next cursor.</summary>
    Task<ZdfLetterPageResult> FetchLetterPageAsync(int letterIndex, string? cursor, CancellationToken ct = default);

    /// <summary>Fetches episodes for one season of a show.</summary>
    Task<ZdfSeasonResult> FetchSeasonAsync(string canonical, int seasonIndex, string? cursor, CancellationToken ct = default);

    /// <summary>Fetches episodes from a special collection (Films, Docs, Series, Sports).</summary>
    Task<ZdfSeasonResult> FetchCollectionAsync(string collectionId, string? cursor, CancellationToken ct = default);

    /// <summary>Fetches canonical IDs from the day search endpoint.</summary>
    Task<IReadOnlyList<string>> FetchDaySearchAsync(DateOnly date, CancellationToken ct = default);

    /// <summary>
    /// Fetches and parses one download DTO URL.
    /// Returns the raw JSON (for storage) alongside the typed download data.
    /// </summary>
    Task<(string RawJson, ZdfDownloadRaw Result)?> FetchEpisodeDownloadAsync(string downloadUrl, CancellationToken ct = default);
}

public sealed record ZdfLetterPageResult(IReadOnlyList<ZdfShowRef> Shows, string? NextCursor);
public sealed record ZdfSeasonResult(IReadOnlyList<ZdfEpisodeRef> Episodes, string? NextCursor);

/// <summary>A ZDF show discovered from the A-Z letter page.</summary>
public sealed record ZdfShowRef(
    string Topic,
    string Canonical,
    int SeasonCount,
    bool HasNoSeason,
    string? CollectionId
);

/// <summary>
/// A ZDF episode discovered from a season or collection page.
/// Contains metadata and one or more download URLs keyed by vodMediaType.
/// </summary>
public sealed record ZdfEpisodeRef(
    string Topic,
    string BroadcasterKey,
    string Title,
    string? Description,
    string? WebsiteUrl,
    DateTimeOffset? BroadcastTime,
    IReadOnlyDictionary<string, string> DownloadUrlsByType  // vodMediaType → ptmdTemplate URL
);

/// <summary>Resolved download data from one ptmdTemplate URL (one language variant).</summary>
public sealed record ZdfDownloadRaw(
    TimeSpan? Duration,
    GeoRestriction Geo,
    IReadOnlyList<ZdfStreamRaw> Streams,
    IReadOnlyList<ZdfSubtitleRaw> Subtitles
);

public sealed record ZdfStreamRaw(StreamQuality Quality, StreamLanguage Language, string Url);
public sealed record ZdfSubtitleRaw(StreamLanguage Language, string Url);
