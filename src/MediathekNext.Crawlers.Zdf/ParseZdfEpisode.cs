using MediathekNext.Crawlers.Core;

namespace MediathekNext.Crawlers.Zdf;

public record ParseZdfEpisodeCommand(
    ZdfEpisodeRef Episode,
    ZdfDownloadRaw Download,
    string VodMediaType
);

/// <summary>
/// Pure handler — no I/O, no dependencies.
/// Maps one (ZdfEpisodeRef + ZdfDownloadRaw) into a single CrawlResult.
/// Returns null if the download has no streams.
/// </summary>
public sealed class ParseZdfEpisodeHandler
{
    public CrawlResult? Handle(ParseZdfEpisodeCommand cmd)
    {
        var (ep, dl, vodMediaType) = cmd;
        if (dl.Streams.Count == 0) return null;

        // DGS variant: override all stream languages to GermanDgs
        var streams = vodMediaType.Contains("dgs", StringComparison.OrdinalIgnoreCase)
            ? dl.Streams.Select(s => s with { Language = StreamLanguage.GermanDgs }).ToList()
            : (IReadOnlyList<ZdfStreamRaw>)dl.Streams;

        return new CrawlResult(
            BroadcasterKey:    ep.BroadcasterKey,
            ShowTitle:         ep.Topic.Length > 0 ? ep.Topic : ep.Title,
            ShowExternalId:    null,
            EpisodeTitle:      ep.Title,
            Description:       ep.Description,
            BroadcastTime:     ep.BroadcastTime,
            Duration:          dl.Duration,
            WebsiteUrl:        ep.WebsiteUrl,
            ThumbnailUrl:      null,
            Geo:               dl.Geo,
            EpisodeExternalId: null,
            Streams:           streams.Select(s => new StreamEntry(s.Quality, s.Language, s.Url)).ToList(),
            Subtitles:         dl.Subtitles.Select(s => new SubtitleEntry(s.Language, s.Url)).ToList()
        );
    }
}
