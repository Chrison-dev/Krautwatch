namespace MediathekNext.Domain.Entities;

public class Show
{
    public string Id { get; init; } = default!;
    public string Title { get; init; } = default!;
    public string ChannelId { get; init; } = default!;
    public Channel Channel { get; init; } = default!;
    public ICollection<Episode> Episodes { get; init; } = new List<Episode>();
}
