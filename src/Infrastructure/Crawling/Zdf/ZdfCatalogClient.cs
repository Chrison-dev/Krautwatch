using System.Text.Json;

namespace Krautwatch.Infrastructure.Crawling.Zdf;

/// <summary>An episode found in the ZDF search.</summary>
public sealed record ZdfEpisode(string Title, string Query, DateTimeOffset? EditorialDate, string Canonical);

/// <summary>A resolved progressive stream (MP4). <paramref name="GeoRestricted"/> reflects the PTMD
/// <c>attributes.geoLocation</c> (anything other than "none" = in-region-only, e.g. "dach"/"de") (#45).</summary>
public sealed record ZdfStream(string Quality, string MimeType, string Url, bool GeoRestricted = false);

/// <summary>
/// Reads the ZDF Mediathek API. Search is the REST <c>/search/documents?q=</c> endpoint
/// (episodes come back directly); a stream is resolved episode-doc → <c>ptmd-template</c>
/// (expand <c>{playerId}</c>) → PTMD <c>priorityList</c> → progressive MP4. All requests carry
/// the static <c>Api-Auth: Bearer &lt;key&gt;</c> — ZDF rotates it; update on 401/403 (DR-010 / #13,#16).
/// </summary>
public sealed class ZdfCatalogClient(HttpClient http)
{
    public const string ApiBase = "https://api.zdf.de";
    // Rotates when ZDF ships a new API version — update if requests start returning 401/403.
    private const string AuthKey = "aa3noh4ohz9eeboo8shiesheec9ciequ9Quah7el";
    private const string PlayerId = "android_native_6";

    private const string RelResults = "http://zdf.de/rels/search/results";
    private const string RelTarget = "http://zdf.de/rels/target";
    private const string RelPtmd = "http://zdf.de/rels/streams/ptmd-template";

    /// <summary>Search ZDF for episodes matching <paramref name="query"/> (e.g. "Heute Show").</summary>
    public async Task<IReadOnlyList<ZdfEpisode>> SearchEpisodesAsync(string query, CancellationToken ct = default)
    {
        var url = $"{ApiBase}/search/documents?q={Uri.EscapeDataString(query)}&hasVideo=true&page=1";
        using var doc = await GetJsonAsync(url, ct);
        var episodes = new List<ZdfEpisode>();
        if (doc is null || !doc.RootElement.TryGetProperty(RelResults, out var results)) return episodes;

        foreach (var result in results.EnumerateArray())
        {
            if (!result.TryGetProperty(RelTarget, out var target)) continue;
            if (Str(target, "contentType") != "episode") continue;

            var title = Str(target, "teaserHeadline") ?? Str(target, "title") ?? "";
            var canonical = Str(target, "canonical");
            if (canonical is null) continue;

            DateTimeOffset? editorial = target.TryGetProperty("editorialDate", out var e) && e.ValueKind == JsonValueKind.String
                ? DateTimeOffset.Parse(e.GetString()!) : null;

            episodes.Add(new ZdfEpisode(title, query, editorial, canonical));
        }
        return episodes;
    }

    /// <summary>Resolve the best progressive MP4 for an episode (by its canonical doc path).</summary>
    public async Task<ZdfStream?> ResolveBestMp4Async(string canonical, CancellationToken ct = default)
    {
        using var epDoc = await GetJsonAsync($"{ApiBase}{canonical}", ct);
        if (epDoc is null) return null;

        var template = FindPtmdTemplate(epDoc.RootElement);
        if (template is null) return null;

        var ptmdUrl = $"{ApiBase}{template.Replace("{playerId}", PlayerId)}";
        using var ptmd = await GetJsonAsync(ptmdUrl, ct);
        if (ptmd is null || !ptmd.RootElement.TryGetProperty("priorityList", out var priorities)) return null;

        // attributes.geoLocation.value: "none" = worldwide, otherwise in-region-only ("dach"/"de") (#45).
        var geoRestricted = ptmd.RootElement.TryGetProperty("attributes", out var attrs)
            && attrs.TryGetProperty("geoLocation", out var geo)
            && geo.TryGetProperty("value", out var geoVal)
            && geoVal.ValueKind == JsonValueKind.String
            && !string.Equals(geoVal.GetString(), "none", StringComparison.OrdinalIgnoreCase);

        // Rank MP4 qualities high→low; pick the best available.
        var order = new[] { "fhd", "uhd", "hd", "veryhigh", "high", "low" };
        ZdfStream? best = null;
        var bestRank = int.MaxValue;

        foreach (var prio in priorities.EnumerateArray())
        foreach (var format in prio.GetPropertyOrEmptyArray("formitaeten"))
        {
            var mime = Str(format, "mimeType") ?? "";
            if (!mime.Contains("mp4", StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var quality in format.GetPropertyOrEmptyArray("qualities"))
            {
                var q = Str(quality, "quality") ?? "";
                var rank = Array.IndexOf(order, q);
                if (rank < 0) rank = order.Length;

                if (!quality.TryGetProperty("audio", out var audio) ||
                    !audio.TryGetProperty("tracks", out var tracks)) continue;

                foreach (var track in tracks.EnumerateArray())
                {
                    var uri = Str(track, "uri");
                    if (uri is null) continue;
                    if (rank < bestRank)
                    {
                        bestRank = rank;
                        best = new ZdfStream(q, mime, uri);
                    }
                }
            }
        }
        return best is null ? null : best with { GeoRestricted = geoRestricted };
    }

    /// <summary>Fetch a single ZDF episode's full program data (doc metadata + resolved progressive MP4).</summary>
    public async Task<EpisodeDetail?> FetchEpisodeDetailAsync(ZdfEpisode episode, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync($"{ApiBase}{episode.Canonical}", ct);
        if (doc is null) return null;
        var root = doc.RootElement;

        var title = Str(root, "title") ?? episode.Title;
        var show = root.TryGetProperty("http://zdf.de/rels/brand", out var brand) && brand.TryGetProperty("title", out var bt)
            ? bt.GetString() ?? episode.Query : episode.Query;
        DateTimeOffset? airDate = root.TryGetProperty("editorialDate", out var ed) && ed.ValueKind == JsonValueKind.String
            ? DateTimeOffset.Parse(ed.GetString()!) : episode.EditorialDate;
        var synopsis = Str(root, "leadParagraph");

        var duration = TimeSpan.Zero;
        if (root.TryGetProperty("mainVideoContent", out var mvc) && mvc.TryGetProperty(RelTarget, out var tgt)
            && tgt.TryGetProperty("duration", out var du) && du.ValueKind == JsonValueKind.Number)
            duration = TimeSpan.FromSeconds(du.GetInt32());

        var stream = await ResolveBestMp4Async(episode.Canonical, ct);

        return new EpisodeDetail(title, show, "ZDF", airDate, duration, synopsis, stream?.Url, SubtitleUrl: null,
            GeoRestricted: stream?.GeoRestricted ?? false);
    }

    private static string? FindPtmdTemplate(JsonElement root)
    {
        // Prefer mainVideoContent → target → streams.default; fall back to any ptmd-template.
        string? fallback = null;
        void Walk(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Object)
                foreach (var p in e.EnumerateObject())
                {
                    if (p.Name == RelPtmd && p.Value.ValueKind == JsonValueKind.String)
                        fallback ??= p.Value.GetString();
                    else Walk(p.Value);
                }
            else if (e.ValueKind == JsonValueKind.Array)
                foreach (var i in e.EnumerateArray()) Walk(i);
        }
        Walk(root);
        return fallback;
    }

    private static string? Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Api-Auth", $"Bearer {AuthKey}");
            using var resp = await http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
                return await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (attempt >= 3) return null;
            await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), ct);
        }
    }
}

