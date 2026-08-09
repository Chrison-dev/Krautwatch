using Krautwatch.Domain.Enums;

namespace Krautwatch.Domain.Entities;

public class Episode
{
    public string Id { get; init; } = default!;
    public string Title { get; init; } = default!;
    public string? Description { get; init; }
    public string ShowId { get; init; } = default!;
    public Show Show { get; set; } = default!;
    public DateTimeOffset BroadcastDate { get; init; }
    public TimeSpan Duration { get; init; }
    public DateTimeOffset? AvailableUntil { get; init; }
    public ContentType ContentType { get; init; } = ContentType.Episode;

    /// <summary>
    /// The broadcaster declares this asset geo-restricted (e.g. DACH-only licensed content). The
    /// stream is only reachable from an in-region egress; the Downloader routes such jobs through a
    /// configured proxy (#45). Detected at resolve time — ARD <c>isGeoBlocked</c> / ZDF <c>geoLocation</c>.
    /// </summary>
    public bool GeoRestricted { get; init; }

    /// <summary>
    /// WebVTT subtitle track published alongside the video, or null where the broadcaster offers none
    /// (#20). Persisted at crawl time and fetched as a sidecar when the episode is downloaded.
    /// </summary>
    public string? SubtitleUrl { get; init; }

    // Sonarr numbering model — populated when the broadcaster exposes it; null for pure Daily
    // (air-date-matched) content. BroadcastDate is the air-date key for the Daily regime.
    public int? SeasonNumber { get; init; }
    public int? EpisodeNumber { get; init; }
    public int? AbsoluteEpisodeNumber { get; init; }

    public ICollection<EpisodeStream> Streams { get; init; } = new List<EpisodeStream>();
}
