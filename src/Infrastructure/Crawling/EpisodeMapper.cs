using System.Text;
using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Enums;

namespace Krautwatch.Infrastructure.Crawling;

/// <summary>
/// Maps a broadcaster's normalized <see cref="EpisodeDetail"/> into the Domain graph
/// (Channel → Show → Episode → EpisodeStream). Lives in Infrastructure — the only layer allowed
/// to see both <see cref="EpisodeDetail"/> and the Domain entities — which keeps the hexagon intact.
///
/// IDs are deterministic and derived from the broadcaster's native id so re-crawls upsert in place:
///   Channel = providerKey · Show = "{providerKey}:{slug(title)}" · Episode = "{providerKey}:{nativeId}".
/// </summary>
internal static class EpisodeMapper
{
    public static Channel Channel(string providerKey, string channelName) => new()
    {
        Id = providerKey,
        Name = channelName,
        ProviderKey = providerKey,
    };

    public static Show Show(string providerKey, string title, Channel channel) => new()
    {
        Id = $"{providerKey}:{Slug(title)}",
        Title = title,
        ChannelId = channel.Id,
        Channel = channel,
    };

    public static Episode Episode(string providerKey, Show show, string nativeId, EpisodeDetail detail)
    {
        var id = $"{providerKey}:{nativeId}";

        // Sonarr numbering: if the title encodes a season/episode, record it and mark the show
        // Standard; otherwise it stays Daily (air-date matched).
        var (season, number) = EpisodeNumbering.Parse(detail.Title);
        if (season is not null && number is not null)
            show.SeriesType = SeriesType.Standard;

        var episode = new Episode
        {
            Id = id,
            Title = detail.Title,
            Description = Truncate(detail.Synopsis, 5000),
            ShowId = show.Id,
            Show = show,
            BroadcastDate = detail.AirDate ?? DateTimeOffset.MinValue,
            Duration = detail.Duration,
            ContentType = ContentType.Episode,
            SeasonNumber = season,
            EpisodeNumber = number,
            GeoRestricted = detail.GeoRestricted,
            SubtitleUrl = detail.SubtitleUrl,
        };

        if (!string.IsNullOrWhiteSpace(detail.StreamUrl))
            episode.Streams.Add(new EpisodeStream
            {
                Id = $"{id}:v",
                EpisodeId = id,
                Quality = VideoQuality.High, // EpisodeDetail carries the single best MP4; quality label is resolved upstream
                Url = detail.StreamUrl,
                Format = "mp4",
            });

        return episode;
    }

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];

    private static string Slug(string value)
    {
        var sb = new StringBuilder(value.Length);
        var lastDash = false;
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) { sb.Append(ch); lastDash = false; }
            else if (!lastDash) { sb.Append('-'); lastDash = true; }
        }
        return sb.ToString().Trim('-') is { Length: > 0 } s ? s : "show";
    }
}
