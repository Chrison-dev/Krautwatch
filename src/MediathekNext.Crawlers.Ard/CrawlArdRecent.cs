using System.Collections.Concurrent;
using System.Diagnostics;
using MediathekNext.Crawlers.Core;
using Microsoft.Extensions.Logging;

namespace MediathekNext.Crawlers.Ard;

public record CrawlArdRecentCommand(int DaysPast = 7);

public sealed class CrawlArdRecentHandler(
    IArdClient client,
    ParseArdEpisodeHandler parser,
    StoreRawResponseHandler rawStore,
    PersistCrawlResultHandler persister,
    ILogger<CrawlArdRecentHandler> log)
{
    private const int Parallelism = 10;

    public async Task<CrawlSummary> HandleAsync(CrawlArdRecentCommand cmd, CancellationToken ct = default)
    {
        var sw       = Stopwatch.StartNew();
        int fetched  = 0, persisted = 0, errors = 0;

        var combos = Enumerable.Range(0, cmd.DaysPast)
            .SelectMany(i =>
            {
                var date = DateOnly.FromDateTime(DateTime.Today.AddDays(-i));
                return ArdConstants.DayClients.Select(c => (date, client: c));
            })
            .ToList();

        // Step 1: Collect item IDs from day pages (parallel)
        var itemIds = new ConcurrentDictionary<string, byte>();
        await Parallel.ForEachAsync(combos, Opts(ct), async (combo, t) =>
        {
            foreach (var id in await client.FetchDayItemIdsAsync(combo.client, combo.date, t))
                itemIds.TryAdd(id, 0);
        });
        log.LogInformation("ARD recent ({Days}d): {Count} item IDs", cmd.DaysPast, itemIds.Count);

        // Step 2: Fetch, store raw, parse, and persist each episode
        await Parallel.ForEachAsync(itemIds.Keys, Opts(ct), async (itemId, t) =>
        {
            try
            {
                int n = await ProcessItemAsync(itemId, t);
                Interlocked.Add(ref persisted, n);
                Interlocked.Increment(ref fetched);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                log.LogError(ex, "ARD recent: error processing {ItemId}", itemId);
                Interlocked.Increment(ref errors);
            }
        });

        log.LogInformation("ARD recent done: {Fetched} fetched, {Persisted} persisted, {Errors} errors in {Elapsed}",
            fetched, persisted, errors, sw.Elapsed);
        return new CrawlSummary("ard", fetched, persisted, errors, sw.Elapsed);
    }

    private async Task<int> ProcessItemAsync(string itemId, CancellationToken ct)
    {
        var result = await client.FetchEpisodeAsync(itemId, ct);
        if (result is null) return 0;

        var (rawJson, episode) = result.Value;
        await rawStore.HandleAsync(new StoreRawResponseCommand("ard", itemId, rawJson), ct);

        int count = 0;
        foreach (var r in parser.Handle(new ParseArdEpisodeCommand(episode)))
        {
            await persister.HandleAsync(new PersistCrawlResultCommand(r), ct);
            count++;
        }

        foreach (var relatedId in episode.RelatedItemIds)
            count += await ProcessItemAsync(relatedId, ct);

        return count;
    }

    private static ParallelOptions Opts(CancellationToken ct) =>
        new() { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct };
}
