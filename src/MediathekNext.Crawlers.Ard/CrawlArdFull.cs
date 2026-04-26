using System.Collections.Concurrent;
using System.Diagnostics;
using MediathekNext.Crawlers.Core;
using Microsoft.Extensions.Logging;

namespace MediathekNext.Crawlers.Ard;

public record CrawlArdFullCommand;

public sealed class CrawlArdFullHandler(
    IArdClient client,
    ParseArdEpisodeHandler parser,
    StoreRawResponseHandler rawStore,
    PersistCrawlResultHandler persister,
    ILogger<CrawlArdFullHandler> log)
{
    private const int Parallelism = 10;

    public async Task<CrawlSummary> HandleAsync(CrawlArdFullCommand _, CancellationToken ct = default)
    {
        var sw       = Stopwatch.StartNew();
        int fetched  = 0, persisted = 0, errors = 0;

        // Step 1: Fetch topic URLs for all clients (parallel)
        var urlBatches = await Task.WhenAll(
            ArdConstants.TopicClients.Select(c => client.FetchTopicUrlsAsync(c, ct)));
        var topicUrls = urlBatches.SelectMany(x => x).Distinct().ToList();
        log.LogInformation("ARD full: {Count} topic URLs", topicUrls.Count);

        // Step 2: Fetch item IDs from each topic URL (parallel, bounded)
        var itemIds = new ConcurrentDictionary<string, byte>();
        await Parallel.ForEachAsync(topicUrls, Opts(ct), async (url, t) =>
        {
            foreach (var id in await client.FetchTopicItemIdsAsync(url, t))
                itemIds.TryAdd(id, 0);
        });
        log.LogInformation("ARD full: {Count} item IDs", itemIds.Count);

        // Step 3: Fetch, store raw, parse, and persist each episode
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
                log.LogError(ex, "ARD full: error processing {ItemId}", itemId);
                Interlocked.Increment(ref errors);
            }
        });

        log.LogInformation("ARD full done: {Fetched} fetched, {Persisted} persisted, {Errors} errors in {Elapsed}",
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
