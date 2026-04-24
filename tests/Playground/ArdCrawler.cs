using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mediathek.Models;

namespace Mediathek.Crawlers.Ard;

/// <summary>
/// Crawls ARD, BR, HR, MDR, NDR, RBB, SR, SWR, WDR, ONE, ARD alpha, tagesschau24, phoenix, funk.
///
/// Translated from ArdCrawler.java. Two modes:
///   Full   – A-Z topics -> compilations -> item details
///   Recent – last N day pages -> item details (fast refresh)
///
/// No authentication required.
/// </summary>
public class ArdCrawler(HttpClient http, ILogger<ArdCrawler> log)
{
    private const int Parallelism = 10;

    // ── Entry points ──────────────────────────────────────────────────────────

    public async IAsyncEnumerable<CrawlResult> CrawlFullAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // Step 1: Get topic compilation URLs for every client (parallel)
        var compilationUrls = new ConcurrentDictionary<string, byte>();
        await Parallel.ForEachAsync(ArdConstants.TopicClients, Opts(ct), async (client, token) =>
        {
            var topicsUrl = string.Format(ArdConstants.TopicsUrl, client);
            var json      = await GetAsync(topicsUrl, token);
            if (json is null) return;

            var urls = ArdParser.ParseTopicsPage(json.Value, client);
            foreach (var u in urls) compilationUrls.TryAdd(u, 0);
            log.LogDebug("ARD: {Client} -> {Count} compilation URLs", client, urls.Count);
        });
        log.LogInformation("ARD: {Count} compilation URLs total", compilationUrls.Count);

        // Step 2: Each compilation URL -> item IDs (parallel)
        var itemIds = new ConcurrentDictionary<string, byte>();
        await Parallel.ForEachAsync(compilationUrls.Keys, Opts(ct), async (compilationUrl, token) =>
        {
            await foreach (var id in FetchCompilationItemIdsAsync(compilationUrl, token))
                itemIds.TryAdd(id, 0);
        });
        log.LogInformation("ARD: {Count} item IDs from compilations", itemIds.Count);

        // Step 3: Each item ID -> episode (parallel)
        foreach (var r in await FetchAllItemsAsync(itemIds.Keys, ct))
            yield return r;
    }

    public async IAsyncEnumerable<CrawlResult> CrawlRecentAsync(
        int daysPast = 7,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // Step 1: Parallel day-page fetches for all days × clients
        var itemIds = new ConcurrentDictionary<string, byte>();
        var combos  = Enumerable.Range(0, daysPast)
            .SelectMany(i =>
            {
                var day = DateTime.Today.AddDays(-i).ToString("yyyy-MM-dd");
                return ArdConstants.DayClients.Select(client => (day, client));
            })
            .ToList();

        await Parallel.ForEachAsync(combos, Opts(ct), async (pair, token) =>
        {
            var url  = string.Format(ArdConstants.DayPageUrl, pair.day, pair.client);
            var json = await GetAsync(url, token);
            if (json is null) return;

            var ids = ArdParser.ParseDayPage(json.Value);
            foreach (var id in ids) itemIds.TryAdd(id, 0);
        });
        log.LogInformation("ARD: {Count} item IDs from day pages", itemIds.Count);

        // Step 2: Parallel item detail fetches
        foreach (var r in await FetchAllItemsAsync(itemIds.Keys, ct))
            yield return r;
    }

    // ── Parallel item fetching ────────────────────────────────────────────────

    private async Task<List<CrawlResult>> FetchAllItemsAsync(IEnumerable<string> ids, CancellationToken ct)
    {
        var results = new ConcurrentBag<CrawlResult>();
        await Parallel.ForEachAsync(ids, Opts(ct), async (id, token) =>
        {
            var fetched = await FetchItemAsync(id, token);
            if (fetched is not null)
                foreach (var r in fetched)
                    results.Add(r);
        });
        return [.. results];
    }

    private static ParallelOptions Opts(CancellationToken ct) =>
        new() { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct };

    // ── Compilation -> item IDs ───────────────────────────────────────────────
    // Compilation URL returns a page with widget items, each having an id
    // Mirrors: ArdTopicGroupsTask -> ArdTopicPageTask -> ArdDayPageTask chain

    private async IAsyncEnumerable<string> FetchCompilationItemIdsAsync(
        string compilationUrl,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var json = await GetAsync(compilationUrl, ct);
        if (json is null) yield break;

        // Compilation pages use the same widget structure as topic pages
        foreach (var widget in json.Value.Array("widgets"))
        {
            // Each widget can be a teaser list
            foreach (var teaser in widget.Array("teasers"))
            {
                var id = teaser.Str("id");
                if (id is not null) yield return id;
            }

            // Or directly a show with an ID
            var directId = widget.Str("id");
            if (directId is not null) yield return directId;
        }
    }

    // ── Item detail -> one or more CrawlResults ───────────────────────────────
    // Mirrors ArdFilmDetailTask.java + ArdFilmDeserializer.java
    // One item can produce multiple results (standard + AD + DGS + OV variants)

    private async Task<List<CrawlResult>?> FetchItemAsync(string itemId, CancellationToken ct)
    {
        var url  = string.Format(ArdConstants.ItemUrl, itemId);
        var json = await GetAsync(url, ct);
        if (json is null) return null;

        var parsed = ArdParser.ParseItemPage(json.Value);
        if (parsed is null) return null;

        var results = new List<CrawlResult>();

        // Standard German
        if (parsed.Streams.Count > 0)
            results.Add(BuildResult(parsed, itemId, parsed.Streams, StreamLanguage.German));

        // Audio description
        if (parsed.StreamsAd.Count > 0)
            results.Add(BuildResult(parsed, itemId,
                parsed.StreamsAd.Select(s => s with { Language = StreamLanguage.GermanAd }).ToList(),
                StreamLanguage.GermanAd, titleSuffix: " (Audiodeskription)"));

        // Sign language
        if (parsed.StreamsDgs.Count > 0)
            results.Add(BuildResult(parsed, itemId,
                parsed.StreamsDgs.Select(s => s with { Language = StreamLanguage.GermanDgs }).ToList(),
                StreamLanguage.GermanDgs, titleSuffix: " (Gebärdensprache)"));

        // Original version
        if (parsed.StreamsOv.Count > 0)
            results.Add(BuildResult(parsed, itemId,
                parsed.StreamsOv.Select(s => s with { Language = StreamLanguage.Original }).ToList(),
                StreamLanguage.Original, titleSuffix: " (Originalversion)"));

        // Recursively fetch related items (numberOfClips > 1)
        foreach (var relatedId in parsed.RelatedItemIds)
        {
            var related = await FetchItemAsync(relatedId, ct);
            if (related is not null) results.AddRange(related);
        }

        return results.Count > 0 ? results : null;
    }

    private static CrawlResult BuildResult(
        ArdParser.ItemResult parsed,
        string itemId,
        List<StreamEntry> streams,
        StreamLanguage language,
        string titleSuffix = "")
    {
        return new CrawlResult(
            BroadcasterKey:    parsed.BroadcasterKey!,
            ShowTitle:         parsed.Topic ?? parsed.Title ?? itemId,
            ShowExternalId:    null,
            EpisodeTitle:      (parsed.Title ?? itemId) + titleSuffix,
            Description:       parsed.Description,
            BroadcastTime:     parsed.BroadcastTime,
            Duration:          parsed.Duration,
            WebsiteUrl:        string.Format(ArdConstants.WebsiteUrl, itemId),
            ThumbnailUrl:      null,
            Geo:               parsed.GeoBlocked ? GeoRestriction.De : GeoRestriction.None,
            EpisodeExternalId: itemId,
            Streams:           streams,
            Subtitles:         parsed.Subtitles
        );
    }

    // ── HTTP ──────────────────────────────────────────────────────────────────

    private async Task<JsonElement?> GetAsync(string url, CancellationToken ct)
    {
        try
        {
            var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                log.LogWarning("ARD HTTP {Status} for {Url}", (int)resp.StatusCode, url);
                return null;
            }
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return doc.RootElement.Clone();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.LogError(ex, "ARD fetch error: {Url}", url);
            return null;
        }
    }
}
