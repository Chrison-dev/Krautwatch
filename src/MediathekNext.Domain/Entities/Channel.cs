namespace MediathekNext.Domain.Entities;

public class Channel
{
    public string Id { get; init; } = default!;
    public string Name { get; init; } = default!;
    public string ProviderKey { get; init; } = default!;
}
