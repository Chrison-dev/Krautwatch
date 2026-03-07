using MediathekNext.Domain.Enums;

namespace MediathekNext.Domain.Entities;

public class EpisodeStream
{
    public string Id { get; init; } = default!;
    public string EpisodeId { get; init; } = default!;
    public VideoQuality Quality { get; init; }
    public string Url { get; init; } = default!;
    public string Format { get; init; } = default!; // e.g. "mp4", "m3u8"
}
