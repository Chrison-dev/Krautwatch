using System.Diagnostics;
using MediathekNext.Crawlers.Core;
using Microsoft.Extensions.Logging;

namespace MediathekNext.Crawlers.Zdf;

public record CrawlZdfFullCommand;

public sealed class CrawlZdfFullHandler(
    IZdfClient client,
    ParseZdfEpisodeHandler parser,
    StoreRawResponseHandler rawStore,
    PersistCrawlResultHandler persister,
    ILogger<CrawlZdfFullHandler> log)
{
    public async Task<CrawlSummary> HandleAsync(CrawlZdfFullCommand _, CancellationToken ct = default)
    {
        var sw       = Stopwatch.StartNew();
        int fetched  = 0, persisted = 0, errors = 0;

        // Collect all episode refs from A-Z pages and special collections
        var episodeRefs = new List<ZdfEpisodeRef>();

        // Step 1: A-Z letter pages → show refs → expand into episode refs
        for (int letterIdx = 0; letterIdx < ZdfConstants.LetterPageCount; letterIdx++)
        {
            string? cursor = null;
            do
            {
                var letterPage = await client.FetchLetterPageAsync(letterIdx, cursor, ct);
                cursor = letterPage.NextCursor;

                foreach (var show in letterPage.Shows)
                {
                    if (show.HasNoSeason && show.CollectionId is not null)
                    {
                        await ExpandCollectionAsync(show.CollectionId, show.Topic, episodeRefs, ct);
                    }
                    else
                    {
                        for (int seasonIdx = 0; seasonIdx < show.SeasonCount; seasonIdx++)
                            await ExpandSeasonAsync(show.Canonical, show.Topic, seasonIdx, episodeRefs, ct);
                    }
                }
            }
            while (cursor is not null);
        }
        log.LogInformation("ZDF full: {Count} episode refs from A-Z", episodeRefs.Count);

        // Step 2: Special collections (Filme / Dokus / Serien / Sport)
        foreach (var (collectionId, topic) in ZdfConstants.SpecialCollections)
            await ExpandCollectionAsync(collectionId, topic, episodeRefs, ct);

        log.LogInformation("ZDF full: {Count} episode refs total", episodeRefs.Count);

        // Step 3: Resolve each episode ref → store raw + persist
        foreach (var ep in episodeRefs)
        {
            foreach (var (vodMediaType, downloadUrl) in ep.DownloadUrlsByType)
            {
                try
                {
                    var result = await client.FetchEpisodeDownloadAsync(downloadUrl, ct);
                    if (result is null) continue;

                    var (rawJson, download) = result.Value;
                    await rawStore.HandleAsync(new StoreRawResponseCommand("zdf", downloadUrl, rawJson), ct);

                    var crawlResult = parser.Handle(new ParseZdfEpisodeCommand(ep, download, vodMediaType));
                    if (crawlResult is not null)
                    {
                        await persister.HandleAsync(new PersistCrawlResultCommand(crawlResult), ct);
                        persisted++;
                    }

                    fetched++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    log.LogError(ex, "ZDF full: error resolving {Url}", downloadUrl);
                    errors++;
                }
            }
        }

        log.LogInformation("ZDF full done: {Fetched} fetched, {Persisted} persisted, {Errors} errors in {Elapsed}",
            fetched, persisted, errors, sw.Elapsed);
        return new CrawlSummary("zdf", fetched, persisted, errors, sw.Elapsed);
    }

    private async Task ExpandSeasonAsync(
        string canonical, string topic, int seasonIndex,
        List<ZdfEpisodeRef> out_, CancellationToken ct)
    {
        string? cursor = null;
        do
        {
            var season = await client.FetchSeasonAsync(canonical, seasonIndex, cursor, ct);
            // Backfill topic into episode refs that have an empty one
            out_.AddRange(season.Episodes.Select(e =>
                e.Topic.Length > 0 ? e : e with { Topic = topic }));
            cursor = season.NextCursor;
        }
        while (cursor is not null);
    }

    private async Task ExpandCollectionAsync(
        string collectionId, string topic,
        List<ZdfEpisodeRef> out_, CancellationToken ct)
    {
        string? cursor = null;
        do
        {
            var result = await client.FetchCollectionAsync(collectionId, cursor, ct);
            out_.AddRange(result.Episodes.Select(e =>
                e.Topic.Length > 0 ? e : e with { Topic = topic }));
            cursor = result.NextCursor;
        }
        while (cursor is not null);
    }
}
