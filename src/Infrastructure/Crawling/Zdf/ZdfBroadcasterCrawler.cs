using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Interfaces;

namespace Krautwatch.Infrastructure.Crawling.Zdf;

/// <summary>
/// Adapts <see cref="ZdfCatalogClient"/> to the <see cref="IBroadcasterCrawler"/> port. ZDF search
/// returns episodes directly; each is grouped under its brand (the "show"). A single Channel and one
/// Show instance per distinct brand are reused across the batch so the graph-safe upsert sees shared
/// entities. Only streamable episodes (a resolved progressive MP4) are returned.
/// </summary>
public sealed class ZdfBroadcasterCrawler(ZdfCatalogClient client) : IBroadcasterCrawler
{
    private const string ProviderKeyValue = "zdf";

    public string ProviderKey => ProviderKeyValue;

    public async Task<IReadOnlyList<Episode>> CrawlShowAsync(string showQuery, CancellationToken ct = default)
    {
        var hits = await client.SearchEpisodesAsync(showQuery, ct);
        if (hits.Count == 0) return [];

        var channel = EpisodeMapper.Channel(ProviderKeyValue, "ZDF");
        var showsByTitle = new Dictionary<string, Show>(StringComparer.OrdinalIgnoreCase);

        var episodes = new List<Episode>(hits.Count);
        foreach (var hit in hits)
        {
            var detail = await client.FetchEpisodeDetailAsync(hit, ct);
            if (detail?.StreamUrl is null) continue;

            if (!showsByTitle.TryGetValue(detail.Show, out var show))
            {
                show = EpisodeMapper.Show(ProviderKeyValue, detail.Show, channel);
                showsByTitle[detail.Show] = show;
            }

            episodes.Add(EpisodeMapper.Episode(ProviderKeyValue, show, NativeId(hit.Canonical), detail));
        }
        return episodes;
    }

    // The ZDF canonical (e.g. "/content/documents/…") is the stable native id; strip the leading slash.
    private static string NativeId(string canonical) => canonical.TrimStart('/');
}
