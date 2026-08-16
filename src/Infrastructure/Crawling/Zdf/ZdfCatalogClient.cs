using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Krautwatch.Infrastructure.Crawling.Zdf;

/// <summary>An episode found in the ZDF search.</summary>
public sealed record ZdfEpisode(string Title, string Query, DateTimeOffset? EditorialDate, string Canonical);

/// <summary>A resolved progressive stream (MP4). <paramref name="GeoRestricted"/> reflects the PTMD
/// <c>attributes.geoLocation</c> (anything other than "none" = in-region-only, e.g. "dach"/"de") (#45).</summary>
public sealed record ZdfStream(
    string Quality, string MimeType, string Url, bool GeoRestricted = false, string? SubtitleUrl = null);

/// <summary>
/// Reads the ZDF Mediathek API. Search is the REST <c>/search/documents?q=</c> endpoint
/// (episodes come back directly); a stream is resolved episode-doc → <c>ptmd-template</c>
/// (expand <c>{playerId}</c>) → PTMD <c>priorityList</c> → progressive MP4. All requests carry a
/// static <c>Api-Auth: Bearer &lt;key&gt;</c>, which ZDF rotates — see <see cref="ZdfOptions"/>
/// (DR-010 / #13, #16).
/// </summary>
public sealed class ZdfCatalogClient(
    HttpClient http,
    ZdfOptions? options = null,
    ZdfAuthState? authState = null,
    ILogger<ZdfCatalogClient>? logger = null)
{
    public const string ApiBase = "https://api.zdf.de";
    private const string PlayerId = "android_native_6";

    // Defaults keep the client newable in tests and live tests, where the key is not the subject.
    private readonly ZdfOptions _options = options ?? new ZdfOptions();
    private readonly ZdfAuthState _authState = authState ?? new ZdfAuthState();
    private readonly ILogger _logger = logger ?? NullLogger<ZdfCatalogClient>.Instance;

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
        return best is null
            ? null
            : best with { GeoRestricted = geoRestricted, SubtitleUrl = FindWebVtt(ptmd.RootElement) };
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

        return new EpisodeDetail(title, show, "ZDF", airDate, duration, synopsis, stream?.Url,
            SubtitleUrl: stream?.SubtitleUrl,
            GeoRestricted: stream?.GeoRestricted ?? false);
    }

    /// <summary>
    /// Picks the WebVTT caption track from a PTMD document (#20). ZDF publishes each caption in several
    /// formats — typically EBU-TT-D XML alongside WebVTT — and only WebVTT is useful as a sidecar for
    /// Sonarr, Plex and Jellyfin.
    /// </summary>
    /// <remarks>
    /// Deliberately lenient about how the format is spelled: matching on the declared format <em>or</em> a
    /// <c>.vtt</c> URI means a rename on ZDF's side degrades to "no subtitles", never to writing an XML
    /// file named <c>.vtt</c>. Verified against the live API by <c>Live.Tests</c>.
    /// </remarks>
    internal static string? FindWebVtt(JsonElement ptmdRoot)
    {
        if (!ptmdRoot.TryGetProperty("captions", out var captions)
            || captions.ValueKind != JsonValueKind.Array)
            return null;

        string? fallback = null;

        foreach (var caption in captions.EnumerateArray())
        {
            var uri = Str(caption, "uri");
            if (string.IsNullOrWhiteSpace(uri)) continue;

            var format = Str(caption, "format") ?? "";
            if (format.Contains("webvtt", StringComparison.OrdinalIgnoreCase))
                return uri;

            if (uri.Contains(".vtt", StringComparison.OrdinalIgnoreCase))
                fallback ??= uri;
        }

        return fallback;
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

    /// <summary>
    /// GETs a ZDF document, retrying transient failures.
    /// </summary>
    /// <remarks>
    /// A 401/403 is <b>not</b> transient and is not retried: the key is either accepted or it is not,
    /// so two further attempts only slow every crawl down before failing anyway. It throws rather than
    /// returning null, because null here means "nothing to crawl" — which is exactly how a rotated key
    /// used to turn a broken indexer into a silent one (#13).
    /// </remarks>
    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Api-Auth", $"Bearer {_options.ApiAuthKey}");
            using var resp = await http.SendAsync(req, ct);

            if (resp.IsSuccessStatusCode)
            {
                _authState.RecordSuccess();
                return await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            }

            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                _authState.RecordRejection(resp.StatusCode, DateTimeOffset.UtcNow);

                _logger.LogError(
                    "ZDF API rejected our Api-Auth key ({StatusCode}) — it has most likely been rotated. " +
                    "Set {Section}:{Setting} to the current value. ZDF crawling produces nothing until then.",
                    (int)resp.StatusCode, ZdfOptions.SectionName, nameof(ZdfOptions.ApiAuthKey));

                throw new ZdfAuthRejectedException(resp.StatusCode);
            }

            if (attempt >= 3) return null;
            await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), ct);
        }
    }
}

