using System.Diagnostics;
using MediathekNext.Crawlers.Core;
using Microsoft.Extensions.Logging;

namespace MediathekNext.Crawlers.Zdf;

public record CrawlZdfRecentCommand(int DaysPast = 7);

public sealed class CrawlZdfRecentHandler(
    IZdfClient client,
    ParseZdfEpisodeHandler parser,
    StoreRawResponseHandler rawStore,
    PersistCrawlResultHandler persister,
    ILogger<CrawlZdfRecentHandler> log)
{
    public async Task<CrawlSummary> HandleAsync(CrawlZdfRecentCommand cmd, CancellationToken ct = default)
    {
        var sw       = Stopwatch.StartNew();
        int fetched  = 0, persisted = 0, errors = 0;

        // Step 1: Collect canonical IDs from day search for each past day
        var canonicals = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < cmd.DaysPast; i++)
        {
            var date = DateOnly.FromDateTime(DateTime.Today.AddDays(-i));
            foreach (var c in await client.FetchDaySearchAsync(date, ct))
                canonicals.Add(c);
        }
        log.LogInformation("ZDF recent ({Days}d): {Count} canonicals", cmd.DaysPast, canonicals.Count);

        // Step 2: Fetch most-recent season (index 0) for each canonical
        var episodeRefs = new List<ZdfEpisodeRef>();
        foreach (var canonical in canonicals)
        {
            string? cursor = null;
            do
            {
                var season = await client.FetchSeasonAsync(canonical, seasonIndex: 0, cursor, ct);
                episodeRefs.AddRange(season.Episodes);
                cursor = season.NextCursor;
            }
            while (cursor is not null);
        }
        log.LogInformation("ZDF recent: {Count} episode refs", episodeRefs.Count);

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
                    log.LogError(ex, "ZDF recent: error resolving {Url}", downloadUrl);
                    errors++;
                }
            }
        }

        log.LogInformation("ZDF recent done: {Fetched} fetched, {Persisted} persisted, {Errors} errors in {Elapsed}",
            fetched, persisted, errors, sw.Elapsed);
        return new CrawlSummary("zdf", fetched, persisted, errors, sw.Elapsed);
    }
}
