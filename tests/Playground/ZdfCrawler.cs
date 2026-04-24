using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mediathek.Models;

namespace Mediathek.Crawlers.Zdf;

/// <summary>
/// Crawls ZDF, ZDFneo, ZDFinfo, ZDFtivi.
///
/// Translated from ZdfCrawler.java. Two modes:
///   Full  – A-Z letter pages + special collections -> all current episodes
///   Recent – last N day pages only (fast refresh)
///
/// Auth: single hardcoded bearer token in ZdfConstants.AuthKey.
/// When ZDF rotates it, update that constant and redeploy.
/// </summary>
public class ZdfCrawler(HttpClient http, ILogger<ZdfCrawler> log)
{
    // ── Entry points ──────────────────────────────────────────────────────────

    public async IAsyncEnumerable<CrawlResult> CrawlFullAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // Step 1: A-Z letter pages -> topic refs
        var topicRefs = new List<ZdfParser.TopicRef>();
        for (int i = 0; i < ZdfConstants.LetterPageCount; i++)
        {
            string? cursor = null;
            do
            {
                var json = await GetAsync(ZdfUrlBuilder.LetterPage(i, cursor ?? "null"), ct);
                if (json is null) break;
                var (topics, next) = ZdfParser.ParseLetterPage(json.Value);
                topicRefs.AddRange(topics);
                cursor = next;
            } while (cursor is not null);
        }
        log.LogInformation("ZDF: {Count} topic refs from A-Z", topicRefs.Count);

        // Step 2: Special collections (Filme / Dokus / Serien / Sport)
        foreach (var (collectionId, collectionTopic) in ZdfConstants.SpecialCollections)
        {
            await foreach (var result in CrawlSpecialCollectionAsync(collectionId, collectionTopic, ct))
                yield return result;
        }

        // Step 3: Expand each topic ref into episodes
        foreach (var topic in topicRefs)
        {
            await foreach (var result in CrawlTopicRefAsync(topic, ct))
                yield return result;
        }
    }

    public async IAsyncEnumerable<CrawlResult> CrawlRecentAsync(
        int daysPast = 7,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // Day pages: past N days (mirrors ZdfCrawler.getDayUrls with daysFuture=0)
        for (int i = 0; i < daysPast; i++)
        {
            var date = DateOnly.FromDateTime(DateTime.Today.AddDays(-i));
            var json = await GetAsync(ZdfUrlBuilder.DaySearch(date), ct);
            if (json is null) continue;

            // Day search returns { results: [ { id, url, ... } ] }
            // Each result is a show page URL -> we treat it as a topic season fetch
            foreach (var item in json.Value.Array("results"))
            {
                var canonical = item.Str("canonical") ?? item.Str("id");
                if (canonical is null) continue;

                // Fetch season 0 (most recent) for each result
                var topicRef = new ZdfParser.TopicRef("", canonical, 1, false, null);
                await foreach (var result in CrawlTopicRefAsync(topicRef, ct))
                    yield return result;
            }
        }
    }

    // ── Special collections ───────────────────────────────────────────────────

    private async IAsyncEnumerable<CrawlResult> CrawlSpecialCollectionAsync(
        string collectionId, string topic,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        string? cursor = null;
        do
        {
            var url  = ZdfUrlBuilder.SpecialCollection(collectionId, ZdfConstants.EpisodesPageSize, cursor ?? "null");
            var json = await GetAsync(url, ct);
            if (json is null) yield break;

            var (episodes, next) = ZdfParser.ParseTopicSeason(json.Value, topic);
            foreach (var ep in episodes)
            {
                await foreach (var result in ResolveEpisodeAsync(ep, ct))
                    yield return result;
            }
            cursor = next;
        } while (cursor is not null);
    }

    // ── Topic season expansion ────────────────────────────────────────────────

    private async IAsyncEnumerable<CrawlResult> CrawlTopicRefAsync(
        ZdfParser.TopicRef topic,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (topic.HasNoSeason && topic.Id is not null)
        {
            // No season structure — fetch via special collection URL
            await foreach (var r in CrawlSpecialCollectionAsync(topic.Id, topic.Topic, ct))
                yield return r;
            yield break;
        }

        string? cursor = null;
        do
        {
            var url  = ZdfUrlBuilder.TopicSeason(topic.Canonical, topic.SeasonCount - 1, ZdfConstants.EpisodesPageSize, cursor);
            var json = await GetAsync(url, ct);
            if (json is null) yield break;

            var (episodes, next) = ZdfParser.ParseTopicSeason(json.Value, topic.Topic);
            foreach (var ep in episodes)
            {
                await foreach (var result in ResolveEpisodeAsync(ep, ct))
                    yield return result;
            }
            cursor = next;
        } while (cursor is not null);
    }

    // ── Episode resolution: download URL -> stream URLs ───────────────────────
    // Each ZdfFilmDto has a ptmdTemplate URL. We fetch that to get actual MP4 URLs.
    // This mirrors ZdfFilmTask.java -> ZdfDownloadDtoDeserializer

    private async IAsyncEnumerable<CrawlResult> ResolveEpisodeAsync(
        ZdfParser.EpisodeRef ep,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // Each episode can have multiple download URL types (default, DGS, etc.)
        foreach (var (vodMediaType, downloadUrl) in ep.DownloadUrlsByType)
        {
            var json = await GetAsync(downloadUrl, ct);
            if (json is null) continue;

            var download = ZdfParser.ParseDownload(json.Value);
            if (download is null || download.Streams.Count == 0) continue;

            // For DGS variant, mark all streams accordingly
            var streams = vodMediaType.Contains("dgs", StringComparison.OrdinalIgnoreCase)
                ? download.Streams
                    .Select(s => s with { Language = StreamLanguage.GermanDgs })
                    .ToList()
                : download.Streams;

            yield return new CrawlResult(
                BroadcasterKey:    ep.BroadcasterKey,
                ShowTitle:         ep.Topic.Length > 0 ? ep.Topic : ep.Title,
                ShowExternalId:    null,
                EpisodeTitle:      ep.Title,
                Description:       ep.Description,
                BroadcastTime:     ep.Time.HasValue
                                       ? new DateTimeOffset(ep.Time.Value,
                                           TimeSpan.FromHours(1))
                                       : null,
                Duration:          download.Duration,
                WebsiteUrl:        ep.Website,
                ThumbnailUrl:      null,
                Geo:               download.Geo,
                EpisodeExternalId: null,
                Streams:           streams,
                Subtitles:         download.Subtitles
            );
        }
    }

    // ── HTTP ──────────────────────────────────────────────────────────────────

    private async Task<JsonElement?> GetAsync(string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation(ZdfConstants.AuthHeader,
                $"Bearer {ZdfConstants.AuthKey}");
            req.Headers.TryAddWithoutValidation("Accept", "application/json");

            var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                log.LogWarning("ZDF HTTP {Status} for {Url}", (int)resp.StatusCode, url);
                return null;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return doc.RootElement.Clone();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.LogError(ex, "ZDF fetch error: {Url}", url);
            return null;
        }
    }
}
