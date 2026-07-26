using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Interfaces;

namespace Krautwatch.Infrastructure.Crawling.Ard;

/// <summary>
/// Adapts <see cref="ArdCatalogClient"/> to the <see cref="IBroadcasterCrawler"/> port. One instance
/// serves one ARD-platform scope: regular ARD (<c>ard</c>) or KiKA (<c>kika</c>) — the ARD agent
/// registers both. The client's find-show → full-episodes → fetch-detail workflow is encapsulated
/// here; only streamable episodes (a resolved MP4) are returned.
/// </summary>
public sealed class ArdBroadcasterCrawler(
    ArdCatalogClient client,
    string providerKey,
    string scope,
    string channelName) : IBroadcasterCrawler
{
    public string ProviderKey => providerKey;

    public async Task<IReadOnlyList<Episode>> CrawlShowAsync(string showQuery, CancellationToken ct = default)
    {
        var found = await client.FindShowAsync(showQuery, scope, ct);
        if (found is null) return [];

        var ardEpisodes = await client.GetFullEpisodesAsync(found, ct);
        if (ardEpisodes.Count == 0) return [];

        var channel = EpisodeMapper.Channel(providerKey, channelName);
        var show = EpisodeMapper.Show(providerKey, found.Title, channel);

        var episodes = new List<Episode>(ardEpisodes.Count);
        foreach (var ardEpisode in ardEpisodes)
        {
            var detail = await client.FetchEpisodeDetailAsync(ardEpisode, ct);
            if (detail?.StreamUrl is null) continue;
            episodes.Add(EpisodeMapper.Episode(providerKey, show, ardEpisode.Id, detail));
        }
        return episodes;
    }
}
