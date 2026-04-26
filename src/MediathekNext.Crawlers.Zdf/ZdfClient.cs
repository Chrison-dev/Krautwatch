using System.Text.Json;
using System.Text.RegularExpressions;
using MediathekNext.Crawlers.Core;
using Microsoft.Extensions.Logging;

namespace MediathekNext.Crawlers.Zdf;

/// <summary>
/// Fetches data from the ZDF GraphQL API and returns typed models.
/// All JSON parsing lives here; no JsonElement escapes this class.
/// </summary>
internal sealed class ZdfClient(HttpClient http, ILogger<ZdfClient> log) : IZdfClient
{
    public async Task<ZdfLetterPageResult> FetchLetterPageAsync(
        int letterIndex, string? cursor, CancellationToken ct = default)
    {
        var url  = ZdfUrlBuilder.LetterPage(letterIndex, cursor);
        var json = await GetJsonAsync(url, ct);
        if (json is null) return new([], null);
        return ParseLetterPage(json.Value);
    }

    public async Task<ZdfSeasonResult> FetchSeasonAsync(
        string canonical, int seasonIndex, string? cursor, CancellationToken ct = default)
    {
        var url  = ZdfUrlBuilder.TopicSeason(canonical, seasonIndex, ZdfConstants.EpisodesPageSize, cursor);
        var json = await GetJsonAsync(url, ct);
        if (json is null) return new([], null);
        return ParseTopicSeason(json.Value, topic: "");
    }

    public async Task<ZdfSeasonResult> FetchCollectionAsync(
        string collectionId, string? cursor, CancellationToken ct = default)
    {
        var url  = ZdfUrlBuilder.SpecialCollection(collectionId, ZdfConstants.EpisodesPageSize, cursor);
        var json = await GetJsonAsync(url, ct);
        if (json is null) return new([], null);
        return ParseTopicSeason(json.Value, topic: "");
    }

    public async Task<IReadOnlyList<string>> FetchDaySearchAsync(DateOnly date, CancellationToken ct = default)
    {
        var url  = ZdfUrlBuilder.DaySearch(date);
        var json = await GetJsonAsync(url, ct);
        if (json is null) return [];

        var canonicals = new List<string>();
        foreach (var item in json.Value.Array("results"))
        {
            var c = item.Str("canonical") ?? item.Str("id");
            if (c is not null) canonicals.Add(c);
        }
        return canonicals;
    }

    public async Task<(string RawJson, ZdfDownloadRaw Result)?> FetchEpisodeDownloadAsync(
        string downloadUrl, CancellationToken ct = default)
    {
        var rawJson = await GetRawAsync(downloadUrl, ct);
        if (rawJson is null) return null;

        using var doc    = JsonDocument.Parse(rawJson);
        var       result = ParseDownload(doc.RootElement);
        if (result is null) return null;

        return (rawJson, result);
    }

    // ── JSON parsers ──────────────────────────────────────────────────────────

    private static ZdfLetterPageResult ParseLetterPage(JsonElement root)
    {
        var shows = new List<ZdfShowRef>();
        var nodes = root.Path("data", "specialPageByCanonical", "content", "nodes").Array();

        foreach (var node in nodes)
        {
            var sender = node.Path("contentOwner")?.Str("title") ?? "ZDF";
            if (!ZdfConstants.PartnerToKey.ContainsKey(sender)) continue;

            var topic        = node.Str("title") ?? "";
            var countSeasons = node.Str("countSeasons");
            var canonical    = node.Str("canonical");
            var id           = node.Str("id");

            if (countSeasons is null)
            {
                if (id is not null)
                    shows.Add(new ZdfShowRef(topic, id, 0, HasNoSeason: true, CollectionId: id));
            }
            else if (canonical is not null && int.TryParse(countSeasons, out var sc))
            {
                shows.Add(new ZdfShowRef(topic, canonical, sc, HasNoSeason: false, CollectionId: null));
            }
        }

        var pageInfo   = root.Path("data", "specialPageByCanonical", "content", "pageInfo");
        var nextCursor = ParseNextCursor(pageInfo);
        return new ZdfLetterPageResult(shows, nextCursor);
    }

    private static ZdfSeasonResult ParseTopicSeason(JsonElement root, string topic)
    {
        var episodes   = new List<ZdfEpisodeRef>();
        string? cursor = null;

        var data = root.Path("data");
        if (data is null) return new([], null);

        if (data.Value.TryGetProperty("smartCollectionByCanonical", out var scc))
        {
            foreach (var season in scc.Path("seasons", "nodes").Array())
            {
                var eps = season.Path("episodes");
                foreach (var node in eps.Path("nodes").Array())
                    ParseEpisodeNode(node, topic, episodes);
                cursor = ParseNextCursor(eps.Path("pageInfo"));
            }
        }
        else if (data.Value.TryGetProperty("metaCollectionContent", out var mcc))
        {
            foreach (var node in mcc.Array("smartCollections"))
                ParseEpisodeNode(node, topic, episodes);
            cursor = ParseNextCursor(mcc.Path("pageInfo"));
        }

        return new ZdfSeasonResult(episodes, cursor);
    }

    private static void ParseEpisodeNode(JsonElement node, string topic, List<ZdfEpisodeRef> out_)
    {
        var title       = ParseEpisodeTitle(node);
        var website     = node.Str("sharingUrl");
        var time        = ParseEditorialDate(node);
        var description = node.Path("teaser")?.Str("description");
        var sender      = ParseSender(node);

        if (title is null) return;
        if (!ZdfConstants.PartnerToKey.TryGetValue(sender ?? "EMPTY", out var senderKey)) return;

        var downloadUrls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var videoRoot    = node.TryGetProperty("video", out var vEl) ? vEl : node;

        foreach (var media in videoRoot.Path("currentMedia", "nodes").Array())
        {
            var mediaType = media.Str("vodMediaType");
            var url       = media.Str("ptmdTemplate");
            if (mediaType is not null && url is not null)
                downloadUrls[mediaType] = FinalizeUrl(url);
        }

        if (downloadUrls.Count == 0) return;

        // Derive actual show topic from the node's contentOwner if the passed-in topic is empty
        var showTopic = topic.Length > 0 ? topic
            : (node.Path("contentOwner")?.Str("title") ?? title);

        out_.Add(new ZdfEpisodeRef(
            showTopic, senderKey, title, description, website,
            time.HasValue ? new DateTimeOffset(time.Value, TimeSpan.FromHours(1)) : null,
            downloadUrls));
    }

    private static ZdfDownloadRaw? ParseDownload(JsonElement root)
    {
        var duration = ParseDownloadDuration(root);
        var geo      = ParseGeoLocation(root);
        var streams  = ParseVideoUrls(root);
        var subs     = ParseSubtitles(root);
        return new ZdfDownloadRaw(duration, geo, streams, subs);
    }

    private static TimeSpan? ParseDownloadDuration(JsonElement root)
    {
        var ms = root.Path("attributes", "duration")?.Long("value");
        return ms.HasValue ? TimeSpan.FromMilliseconds(ms.Value) : null;
    }

    private static GeoRestriction ParseGeoLocation(JsonElement root)
    {
        var val = root.Path("attributes", "geoLocation")?.Str("value");
        return val?.ToUpperInvariant() switch
        {
            "DE"   => GeoRestriction.De,
            "AT"   => GeoRestriction.At,
            "CH"   => GeoRestriction.Ch,
            "DACH" => GeoRestriction.Dach,
            "WELT" => GeoRestriction.World,
            _      => GeoRestriction.None,
        };
    }

    private static List<ZdfStreamRaw> ParseVideoUrls(JsonElement root)
    {
        var downloads = new List<(StreamLanguage Language, string Uri, int VertRes, StreamQuality Quality)>();

        foreach (var priority   in root.Array("priorityList"))
        foreach (var formitaet  in priority.Array("formitaeten"))
        {
            var mime = formitaet.Str("mimeType");
            if (!string.Equals(mime, "video/mp4", StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var quality in formitaet.Array("qualities"))
            {
                var qualityStr = quality.Str("quality") ?? "";
                var q          = MapZdfQuality(qualityStr);
                var vertRes    = quality.Int("highestVerticalResolution") ?? 0;

                foreach (var track in quality.Path("audio")?.Array("tracks") ?? [])
                {
                    var cls  = track.Str("class") ?? "";
                    var lang = track.Str("language") ?? ZdfConstants.LangDe;
                    var uri  = track.Str("uri");
                    if (uri is null) continue;

                    if (string.Equals(cls, "ad", StringComparison.OrdinalIgnoreCase))
                        lang += "-ad";

                    downloads.Add((MapZdfLanguage(lang), uri, vertRes, q));
                }
            }
        }

        downloads.Sort((a, b) => a.VertRes.CompareTo(b.VertRes));
        return downloads.Select(d => new ZdfStreamRaw(d.Quality, d.Language, d.Uri)).ToList();
    }

    private static List<ZdfSubtitleRaw> ParseSubtitles(JsonElement root)
    {
        var subs = new List<ZdfSubtitleRaw>();
        var seen = new Dictionary<StreamLanguage, bool>();

        foreach (var caption in root.Array("captions"))
        {
            var uri  = caption.Str("uri");
            var lang = caption.Str("language") ?? ZdfConstants.LangDe;
            if (uri is null) continue;

            var langEnum   = MapZdfLanguage(lang);
            var isXml      = uri.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);
            var alreadyXml = seen.GetValueOrDefault(langEnum);

            if (!seen.ContainsKey(langEnum) || (isXml && !alreadyXml))
            {
                if (seen.ContainsKey(langEnum))
                    subs.RemoveAll(s => s.Language == langEnum);
                subs.Add(new ZdfSubtitleRaw(langEnum, uri));
                seen[langEnum] = isXml;
            }
        }
        return subs;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string? ParseEpisodeTitle(JsonElement node)
    {
        var title    = node.Str("title");
        var subtitle = node.Str("subtitle");
        if (title is null) return null;

        var result = subtitle is { Length: > 0 }
            ? $"{title.Trim()} - {subtitle.Trim()}"
            : title.Trim();

        if (node.TryGetProperty("episodeInfo", out var epInfo))
        {
            var season  = epInfo.Int("seasonNumber");
            var episode = epInfo.Int("episodeNumber");
            if (season.HasValue || episode.HasValue)
            {
                var tag = "";
                if (season.HasValue)                   tag += $"S{season:D2}";
                if (season.HasValue && episode.HasValue) tag += "/";
                if (episode.HasValue)                  tag += $"E{episode:D2}";
                result = $"{result} ({tag})".Trim();
            }
        }

        return Regex.Replace(result, @"\(CC.*\) - .* Creative Commons.*", "").Trim();
    }

    private static string? ParseSender(JsonElement node)
    {
        if (node.TryGetProperty("contentOwner", out var co) && co.ValueKind == JsonValueKind.Object)
        {
            var details = co.Str("details");
            if (details is not null) return details;
        }
        var av = node.Path("tracking", "piano", "video");
        return av.Str("av_broadcastdetail") ?? av.Str("av_broadcaster");
    }

    private static DateTime? ParseEditorialDate(JsonElement node)
    {
        var s = node.Str("editorialDate");
        if (s is null) return null;
        return DateTimeOffset.TryParse(s, out var dto) ? dto.LocalDateTime : null;
    }

    private static string FinalizeUrl(string url)
    {
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            url = ZdfConstants.ApiBase + url;
        return url.Replace("{playerId}", "android_native_5");
    }

    private static string? ParseNextCursor(JsonElement? pageInfo)
    {
        if (pageInfo is null) return null;
        var hasNext = pageInfo.Value.Str("hasNextPage");
        if (hasNext != "true") return null;
        return pageInfo.Value.Str("endCursor");
    }

    private static StreamQuality MapZdfQuality(string q) => q.ToLowerInvariant() switch
    {
        "low" or "med" or "medium" or "high" => StreamQuality.Sd,
        "veryhigh" or "hd"                   => StreamQuality.Normal,
        "fhd"                                => StreamQuality.Hd,
        "uhd"                                => StreamQuality.Uhd,
        _                                    => StreamQuality.Sd,
    };

    private static StreamLanguage MapZdfLanguage(string lang) => lang.ToLowerInvariant() switch
    {
        "deu"     => StreamLanguage.German,
        "deu-ad"  => StreamLanguage.GermanAd,
        "deu-dgs" => StreamLanguage.GermanDgs,
        "eng"     => StreamLanguage.English,
        "fra"     => StreamLanguage.French,
        _         => StreamLanguage.Other,
    };

    // ── HTTP helpers ──────────────────────────────────────────────────────────

    private async Task<string?> GetRawAsync(string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation(ZdfConstants.AuthHeader, $"Bearer {ZdfConstants.AuthKey}");
            req.Headers.TryAddWithoutValidation("Accept", "application/json");

            var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                log.LogWarning("ZDF HTTP {Status} for {Url}", (int)resp.StatusCode, url);
                return null;
            }
            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.LogError(ex, "ZDF fetch error: {Url}", url);
            return null;
        }
    }

    private async Task<JsonElement?> GetJsonAsync(string url, CancellationToken ct)
    {
        var raw = await GetRawAsync(url, ct);
        if (raw is null) return null;
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.Clone();
    }
}
