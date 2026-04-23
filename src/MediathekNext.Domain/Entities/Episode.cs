using MediathekNext.Domain.Enums;

namespace MediathekNext.Domain.Entities;

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
    public ICollection<EpisodeStream> Streams { get; init; } = new List<EpisodeStream>();
}
