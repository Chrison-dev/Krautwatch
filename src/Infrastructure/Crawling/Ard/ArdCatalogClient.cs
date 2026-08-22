using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Krautwatch.Infrastructure.Crawling.Ard;

/// <summary>A show resolved from an ARD-platform A-Z catalog.</summary>
public sealed record ArdShow(string Title, string PageId, string PageHref, string? PublicationService);

/// <summary>A full-length episode listed on a show page.</summary>
public sealed record ArdEpisode(
    string Id,
    string Title,
    string ShowTitle,
    DateTimeOffset? BroadcastedOn,
    TimeSpan Duration,
    string ItemHref);

/// <summary>
/// Reads the ARD Mediathek page-gateway. ARD's <c>search-system</c> returns sparse refs that don't
/// resolve, so shows are found the way the site does it: the A-Z catalog (letter widget id is
/// <c>base64("&lt;Brand&gt;.&lt;letter&gt;")</c>) → filter by title → the editorial show page →
/// the "Ganze Folgen" gridlist. KiKA runs on the same platform under its own catalog scope
/// (client <c>kika</c>, brand <c>KiKA</c>) — see DR-010 / issue #10.
/// </summary>
public sealed class ArdCatalogClient(
    HttpClient http,
    ArdOptions? options = null,
    ILogger<ArdCatalogClient>? logger = null)
{
    public const string ApiBase = "https://api.ardmediathek.de/page-gateway";

    // Defaults keep the client newable in tests, where the limits are not the subject.
    private readonly ArdOptions _options = options ?? new ArdOptions();
    private readonly ILogger _logger = logger ?? NullLogger<ArdCatalogClient>.Instance;

    private static (string Path, string Brand) Scope(string client) =>
        client.ToLowerInvariant() switch
        {
            "kika" => ("kika", "KiKA"),
            _      => ("ard",  "ARD"),
        };

    /// <summary>
    /// Find a show by (case-insensitive substring of) its title in the given catalog scope
    /// (<c>ard</c> for regular ARD, <c>kika</c> for KiKA).
    /// </summary>
    public async Task<ArdShow?> FindShowAsync(string query, string client = "ard", CancellationToken ct = default)
    {
        var trimmed = query.Trim();
        if (trimmed.Length == 0) return null;

        var (path, brand) = Scope(client);
        var letter = char.ToLowerInvariant(trimmed[0]);
        var letterId = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{brand}.{letter}")).TrimEnd('=');

        // Walked page by page rather than asked for in one big slice: a letter bucket larger than the
        // page size used to be cut off silently, and "we don't carry that show" is a bad way to say
        // "we stopped reading" (#9).
        var seen = 0;

        for (var page = 0; ; page++)
        {
            using var doc = await GetJsonAsync(
                $"{ApiBase}/widgets/{path}/editorials/{letterId}?pageNumber={page}&pageSize={_options.PageSize}", ct);

            if (doc is null || !doc.RootElement.TryGetProperty("teasers", out var teasers)) return null;

            var onThisPage = 0;

            foreach (var teaser in teasers.EnumerateArray())
            {
                onThisPage++;

                var title = TitleOf(teaser);
                if (title is null || !title.Contains(trimmed, StringComparison.OrdinalIgnoreCase)) continue;

                if (!teaser.TryGetProperty("links", out var links) ||
                    !links.TryGetProperty("target", out var target)) continue;

                var href = target.GetProperty("href").GetString();
                var id = target.GetProperty("id").GetString();
                if (href is null || id is null) continue;

                var pubService = teaser.TryGetProperty("publicationService", out var ps) && ps.TryGetProperty("name", out var psn)
                    ? psn.GetString() : null;

                return new ArdShow(title, id, href, pubService);
            }

            seen += onThisPage;

            var total = doc.RootElement.TryGetProperty("pagination", out var pagination)
                        && pagination.TryGetProperty("totalElements", out var te)
                        && te.ValueKind == JsonValueKind.Number
                ? te.GetInt32()
                : seen;

            // A short page means the end whether or not the count agrees with us.
            if (onThisPage == 0 || seen >= total) return null;
        }
    }

    /// <summary>
    /// Fetch a show's episodes: the "Ganze Folgen" (full episodes) gridlist if present,
    /// otherwise every gridlist teaser on the page.
    /// </summary>
    public async Task<IReadOnlyList<ArdEpisode>> GetFullEpisodesAsync(ArdShow show, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync(show.PageHref, ct);
        if (doc is null || !doc.RootElement.TryGetProperty("widgets", out var widgets))
            return [];

        var preferred = new List<JsonElement>();
        var fallback = new List<JsonElement>();

        foreach (var widget in widgets.EnumerateArray())
        {
            if (!widget.TryGetProperty("teasers", out var teasers) || teasers.ValueKind != JsonValueKind.Array) continue;

            var isFullEpisodes = widget.TryGetProperty("title", out var wt) && wt.GetString() == "Ganze Folgen";
            (isFullEpisodes ? preferred : fallback).Add(widget);
        }

        var chosen = preferred.Count > 0 ? preferred : fallback;

        // A list plus a seen-set rather than a dictionary: order is part of the contract here — the
        // listings are newest-first and the cap keeps the head — and Dictionary does not promise one.
        // The set is what stops a shifting list (ARD publishes constantly) yielding the same teaser
        // from two pages.
        var collected = new EpisodeSet();

        foreach (var widget in chosen)
        {
            if (collected.Count >= _options.MaxEpisodesPerShow) break;
            await CollectWidgetAsync(widget, show, collected, ct);
        }

        return collected.Take(_options.MaxEpisodesPerShow);
    }

    /// <summary>Insertion-ordered, id-deduplicated episodes.</summary>
    private sealed class EpisodeSet
    {
        private readonly List<ArdEpisode> _ordered = [];
        private readonly HashSet<string> _ids = [];

        public int Count => _ordered.Count;

        public void Add(ArdEpisode episode)
        {
            if (_ids.Add(episode.Id))
                _ordered.Add(episode);
        }

        public IReadOnlyList<ArdEpisode> Take(int max) =>
            _ordered.Count <= max ? _ordered : _ordered.Take(max).ToList();
    }

    /// <summary>
    /// Reads a widget's teasers, following its own paging link for whatever the page did not embed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The page embeds only a slice of a large widget — tagesschau's "Bundestag und Parlamente"
    /// reports 1588 items and embeds 35 — so reading the embedded teasers alone silently truncates a
    /// show's history, which to Sonarr is indistinguishable from those episodes not existing (#9).
    /// </para>
    /// <para>
    /// Paging follows <c>links.self.href</c> rather than a URL built here. The issue proposed
    /// <c>/widgets/{scope}/asset/{id}</c>; that answers <b>400</b>. The link the API hands back is
    /// <c>/widgets/{scope}/editorials/{id}</c>, and following it means ARD can move the route without
    /// breaking us.
    /// </para>
    /// </remarks>
    private async Task CollectWidgetAsync(
        JsonElement widget,
        ArdShow show,
        EpisodeSet episodes,
        CancellationToken ct)
    {
        var startedAt = episodes.Count;

        AddTeasers(widget, show, episodes);

        var total = widget.TryGetProperty("pagination", out var pagination)
                    && pagination.TryGetProperty("totalElements", out var te)
                    && te.ValueKind == JsonValueKind.Number
            ? te.GetInt32()
            : 0;

        var pagingUrl = widget.TryGetProperty("links", out var links)
                        && links.TryGetProperty("self", out var self)
                        && self.TryGetProperty("href", out var href)
            ? href.GetString()
            : null;

        // total counts this widget alone, so it is compared against what this widget has contributed
        // rather than the running total across widgets.
        if (pagingUrl is null || total <= episodes.Count - startedAt) return;

        var title = widget.TryGetProperty("title", out var wt) ? wt.GetString() : "(untitled)";

        // The embedded slice is page 0 at a size we did not choose, so the walk restarts at page 0 with
        // ours and lets the id-keyed dictionary absorb the overlap. Page 0 therefore usually adds
        // nothing new, which is why "added nothing" cannot on its own mean "stop".
        // Bounded by the global ceiling, not this widget's share of it: a show whose episodes are
        // spread over several gridlists must not multiply the cap by the number of widgets.
        var wanted = Math.Min(startedAt + total, _options.MaxEpisodesPerShow);
        var barren = 0;

        for (var page = 0; episodes.Count < wanted; page++)
        {
            var before = episodes.Count;

            using var doc = await GetJsonAsync(WithPaging(pagingUrl, page, _options.PageSize), ct);
            if (doc is null) return;

            AddTeasers(doc.RootElement, show, episodes);

            // Two pages running that tell us nothing new: the list is either exhausted early or
            // repeating itself, and either way more requests will not help. One such page is normal
            // (it is the overlap with the embedded slice), two is a dead end.
            barren = episodes.Count == before ? barren + 1 : 0;
            if (barren >= 2) break;
        }

        if (total > _options.MaxEpisodesPerShow)
        {
            _logger.LogInformation(
                "'{Show}' widget '{Widget}' lists {Total} items; kept the newest {Kept} " +
                "(Ard:MaxEpisodesPerShow).", show.Title, title, total, _options.MaxEpisodesPerShow);
        }
    }

    private static void AddTeasers(JsonElement source, ArdShow show, EpisodeSet episodes)
    {
        if (!source.TryGetProperty("teasers", out var teasers) || teasers.ValueKind != JsonValueKind.Array)
            return;

        foreach (var teaser in teasers.EnumerateArray())
        {
            var episode = ToEpisode(teaser, show.Title);
            if (episode is not null)
                episodes.Add(episode);
        }
    }

    /// <summary>Rewrites <c>pageNumber</c>/<c>pageSize</c> on a link the API gave us.</summary>
    internal static string WithPaging(string url, int pageNumber, int pageSize)
    {
        var split = url.Split('?', 2);
        var query = split.Length == 2 ? split[1] : "";

        var parts = query
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !p.StartsWith("pageNumber=", StringComparison.OrdinalIgnoreCase)
                     && !p.StartsWith("pageSize=", StringComparison.OrdinalIgnoreCase))
            .Append($"pageNumber={pageNumber}")
            .Append($"pageSize={pageSize}");

        return $"{split[0]}?{string.Join('&', parts)}";
    }

    /// <summary>Fetch a single episode's full program data (item page: metadata + progressive MP4 + subtitle).</summary>
    public async Task<EpisodeDetail?> FetchEpisodeDetailAsync(ArdEpisode episode, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync(episode.ItemHref, ct);
        if (doc is null || !doc.RootElement.TryGetProperty("widgets", out var widgets)) return null;

        JsonElement w = default;
        foreach (var candidate in widgets.EnumerateArray()) { w = candidate; break; }
        if (w.ValueKind != JsonValueKind.Object) return null;

        var title = TitleOf(w) ?? episode.Title;
        var broadcaster = w.TryGetProperty("publicationService", out var ps) && ps.TryGetProperty("name", out var bn)
            ? bn.GetString() ?? "ARD" : "ARD";
        DateTimeOffset? airDate = w.TryGetProperty("broadcastedOn", out var b) && b.ValueKind == JsonValueKind.String
            ? DateTimeOffset.Parse(b.GetString()!) : episode.BroadcastedOn;
        var synopsis = w.TryGetProperty("synopsis", out var sy) && sy.ValueKind == JsonValueKind.String ? sy.GetString() : null;

        var (streamUrl, subtitleUrl, geoBlocked) = ParseMedia(w);

        return new EpisodeDetail(title, episode.ShowTitle, broadcaster, airDate, episode.Duration, synopsis, streamUrl, subtitleUrl, geoBlocked);
    }

    // mcV6: mediaCollection.embedded.streams[].media[] (video/mp4 by resolution) + subtitles[].sources[] (webvtt).
    // isGeoBlocked (on the embedded mediaCollection) flags DACH-only assets (#45).
    private static (string? Stream, string? Subtitle, bool GeoBlocked) ParseMedia(JsonElement widget)
    {
        if (!widget.TryGetProperty("mediaCollection", out var mcOuter) ||
            !mcOuter.TryGetProperty("embedded", out var mc)) return (null, null, false);

        var geoBlocked = mc.TryGetProperty("isGeoBlocked", out var gb) && gb.ValueKind == JsonValueKind.True;

        string? bestMp4 = null; var bestRes = -1;
        if (mc.TryGetProperty("streams", out var streams))
            foreach (var s in streams.EnumerateArray())
            foreach (var m in s.GetPropertyOrEmptyArray("media"))
            {
                var mime = m.TryGetProperty("mimeType", out var mt) ? mt.GetString() : null;
                if (mime != "video/mp4") continue;
                var res = m.TryGetProperty("maxHResolutionPx", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt32() : 0;
                var url = m.TryGetProperty("url", out var u) ? u.GetString() : null;
                if (url is not null && res > bestRes) { bestRes = res; bestMp4 = url; }
            }

        string? subtitle = null;
        if (mc.TryGetProperty("subtitles", out var subs))
            foreach (var sub in subs.EnumerateArray())
            foreach (var src in sub.GetPropertyOrEmptyArray("sources"))
                if (src.TryGetProperty("kind", out var k) && k.GetString() == "webvtt" &&
                    src.TryGetProperty("url", out var su)) { subtitle = su.GetString(); break; }

        return (bestMp4, subtitle, geoBlocked);
    }

    private static ArdEpisode? ToEpisode(JsonElement teaser, string showTitle)
    {
        if (!teaser.TryGetProperty("links", out var links) ||
            !links.TryGetProperty("target", out var target)) return null;
        var href = target.TryGetProperty("href", out var h) ? h.GetString() : null;
        var id = teaser.TryGetProperty("id", out var i) ? i.GetString() : null;
        if (href is null || id is null) return null;

        var title = TitleOf(teaser) ?? "";
        var duration = teaser.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
            ? TimeSpan.FromSeconds(d.GetInt32()) : TimeSpan.Zero;
        DateTimeOffset? broadcast = teaser.TryGetProperty("broadcastedOn", out var b) && b.ValueKind == JsonValueKind.String
            ? DateTimeOffset.Parse(b.GetString()!) : null;

        return new ArdEpisode(id, title, showTitle, broadcast, duration, href);
    }

    private static string? TitleOf(JsonElement teaser)
    {
        foreach (var key in new[] { "longTitle", "mediumTitle", "shortTitle", "title" })
            if (teaser.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        return null;
    }

    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var resp = await http.GetAsync(url, ct);
            if (resp.IsSuccessStatusCode)
            {
                var stream = await resp.Content.ReadAsStreamAsync(ct);
                return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            }
            if (attempt >= 3) return null;
            await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), ct); // transient backoff
        }
    }
}
