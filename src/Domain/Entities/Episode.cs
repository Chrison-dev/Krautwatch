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

    // Sonarr numbering model — populated when the broadcaster exposes it; null for pure Daily
    // (air-date-matched) content. BroadcastDate is the air-date key for the Daily regime.
    public int? SeasonNumber { get; init; }
    public int? EpisodeNumber { get; init; }
    public int? AbsoluteEpisodeNumber { get; init; }

    public ICollection<EpisodeStream> Streams { get; init; } = new List<EpisodeStream>();
}
