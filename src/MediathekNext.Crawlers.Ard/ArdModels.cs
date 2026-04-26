using MediathekNext.Crawlers.Core;

namespace MediathekNext.Crawlers.Ard;

public interface IArdClient
{
    /// <summary>Fetches the A-Z topic compilation URLs for one ARD client key.</summary>
    Task<IReadOnlyList<string>> FetchTopicUrlsAsync(string clientKey, CancellationToken ct = default);

    /// <summary>Fetches all item IDs from a single topic compilation URL.</summary>
    Task<IReadOnlyList<string>> FetchTopicItemIdsAsync(string topicUrl, CancellationToken ct = default);

    /// <summary>Fetches item IDs from the EPG day page for one client on one day.</summary>
    Task<IReadOnlyList<string>> FetchDayItemIdsAsync(string clientKey, DateOnly date, CancellationToken ct = default);

    /// <summary>
    /// Fetches and parses the detail page for one item.
    /// Returns the raw JSON (for storage) alongside the typed episode model.
    /// Returns null if the item is not found or cannot be parsed.
    /// </summary>
    Task<(string RawJson, ArdEpisodeRaw Episode)?> FetchEpisodeAsync(string itemId, CancellationToken ct = default);
}

/// <summary>
/// Typed representation of one ARD item detail page, with streams already
/// separated by language variant. Produced by ArdClient, consumed by ParseArdEpisodeHandler.
/// </summary>
public sealed record ArdEpisodeRaw(
    string ItemId,
    string BroadcasterKey,
    string ShowTitle,
    string EpisodeTitle,
    string? Description,
    DateTimeOffset? BroadcastTime,
    TimeSpan? Duration,
    bool GeoBlocked,
    IReadOnlyList<ArdStreamRaw> Streams,      // Standard German
    IReadOnlyList<ArdStreamRaw> StreamsAd,    // Audio description
    IReadOnlyList<ArdStreamRaw> StreamsDgs,   // Sign language (Gebärdensprache)
    IReadOnlyList<ArdStreamRaw> StreamsOv,    // Original version
    IReadOnlyList<string> SubtitleUrls,
    IReadOnlyList<string> RelatedItemIds
);

public sealed record ArdStreamRaw(StreamQuality Quality, string Url, bool IsHls = false);
