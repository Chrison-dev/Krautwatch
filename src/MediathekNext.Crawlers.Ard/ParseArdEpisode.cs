using MediathekNext.Crawlers.Core;

namespace MediathekNext.Crawlers.Ard;

public record ParseArdEpisodeCommand(ArdEpisodeRaw Episode);

/// <summary>
/// Pure handler — no I/O, no dependencies.
/// Maps one ArdEpisodeRaw into up to four CrawlResults (one per language variant).
/// </summary>
public sealed class ParseArdEpisodeHandler
{
    public IReadOnlyList<CrawlResult> Handle(ParseArdEpisodeCommand cmd)
    {
        var ep      = cmd.Episode;
        var results = new List<CrawlResult>(4);

        if (ep.Streams.Count > 0)
            results.Add(Build(ep, ep.Streams, StreamLanguage.German));

        if (ep.StreamsAd.Count > 0)
            results.Add(Build(ep, ep.StreamsAd, StreamLanguage.GermanAd, " (Audiodeskription)"));

        if (ep.StreamsDgs.Count > 0)
            results.Add(Build(ep, ep.StreamsDgs, StreamLanguage.GermanDgs, " (Gebärdensprache)"));

        if (ep.StreamsOv.Count > 0)
            results.Add(Build(ep, ep.StreamsOv, StreamLanguage.Original, " (Originalversion)"));

        return results;
    }

    private static CrawlResult Build(
        ArdEpisodeRaw ep,
        IReadOnlyList<ArdStreamRaw> streams,
        StreamLanguage language,
        string titleSuffix = "")
    {
        return new CrawlResult(
            BroadcasterKey:    ep.BroadcasterKey,
            ShowTitle:         ep.ShowTitle,
            ShowExternalId:    null,
            EpisodeTitle:      ep.EpisodeTitle + titleSuffix,
            Description:       ep.Description,
            BroadcastTime:     ep.BroadcastTime,
            Duration:          ep.Duration,
            WebsiteUrl:        string.Format(ArdConstants.WebsiteUrl, ep.ItemId),
            ThumbnailUrl:      null,
            Geo:               ep.GeoBlocked ? GeoRestriction.De : GeoRestriction.None,
            EpisodeExternalId: ep.ItemId + titleSuffix.Replace(" ", "").Replace("(", "").Replace(")", ""),
            Streams:           streams.Select(s => new StreamEntry(s.Quality, language, s.Url, s.IsHls)).ToList(),
            Subtitles:         ep.SubtitleUrls.Select(u => new SubtitleEntry(StreamLanguage.German, u)).ToList()
        );
    }
}
