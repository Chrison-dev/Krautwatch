using System.Text.Json;
using System.Text.RegularExpressions;
using MediathekNext.Crawlers.Core;
using Microsoft.Extensions.Logging;

namespace MediathekNext.Crawlers.Ard;

/// <summary>
/// Fetches data from the ARD API and returns typed models.
/// All JSON parsing lives here; no JsonElement escapes this class.
/// </summary>
internal sealed class ArdClient(HttpClient http, ILogger<ArdClient> log) : IArdClient
{
    public async Task<IReadOnlyList<string>> FetchTopicUrlsAsync(string clientKey, CancellationToken ct = default)
    {
        var url  = string.Format(ArdConstants.TopicsUrl, clientKey);
        var json = await GetJsonAsync(url, ct);
        if (json is null) return [];
        return ParseTopicUrls(json.Value, clientKey);
    }

    public async Task<IReadOnlyList<string>> FetchTopicItemIdsAsync(string topicUrl, CancellationToken ct = default)
    {
        var json = await GetJsonAsync(topicUrl, ct);
        if (json is null) return [];
        return ParseCompilationItemIds(json.Value);
    }

    public async Task<IReadOnlyList<string>> FetchDayItemIdsAsync(
        string clientKey, DateOnly date, CancellationToken ct = default)
    {
        var url  = string.Format(ArdConstants.DayPageUrl, date.ToString("yyyy-MM-dd"), clientKey);
        var json = await GetJsonAsync(url, ct);
        if (json is null) return [];
        return ParseDayPage(json.Value);
    }

    public async Task<(string RawJson, ArdEpisodeRaw Episode)?> FetchEpisodeAsync(
        string itemId, CancellationToken ct = default)
    {
        var url    = string.Format(ArdConstants.ItemUrl, itemId);
        var rawJson = await GetRawAsync(url, ct);
        if (rawJson is null) return null;

        using var doc     = JsonDocument.Parse(rawJson);
        var       episode = ParseItemPage(doc.RootElement, itemId);
        if (episode is null) return null;

        return (rawJson, episode);
    }

    // ── JSON parsers ──────────────────────────────────────────────────────────

    private static IReadOnlyList<string> ParseTopicUrls(JsonElement root, string sender)
    {
        var urls = new List<string>();
        foreach (var widget in root.Array("widgets"))
        {
            var selfId = widget.Path("links", "self")?.Str("id");
            if (selfId is null) continue;
            urls.Add(string.Format(
                ArdConstants.TopicsCompilationUrl,
                sender, selfId, ArdConstants.TopicsCompilationPageSize));
        }
        return urls;
    }

    private static IReadOnlyList<string> ParseCompilationItemIds(JsonElement root)
    {
        var ids = new List<string>();
        foreach (var widget in root.Array("widgets"))
        {
            foreach (var teaser in widget.Array("teasers"))
            {
                var id = teaser.Str("id");
                if (id is not null) ids.Add(id);
            }
            var directId = widget.Str("id");
            if (directId is not null) ids.Add(directId);
        }
        return ids;
    }

    private static IReadOnlyList<string> ParseDayPage(JsonElement root)
    {
        var ids = new List<string>();
        foreach (var channel  in root.Array("channels"))
        foreach (var timeSlot in channel.Array("timeSlots"))
        foreach (var entry    in timeSlot.Array())
        {
            var id = ExtractUrlId(entry);
            if (id is not null) ids.Add(id);
        }
        return ids;
    }

    private static string? ExtractUrlId(JsonElement entry)
    {
        var target = entry.Path("links", "target");
        if (target is not null)
        {
            var id = target.Value.Str("urlId");
            if (id is not null) return id;
        }
        return entry.Str("urlId");
    }

    private static ArdEpisodeRaw? ParseItemPage(JsonElement root, string itemId)
    {
        var widgets = root.Array("widgets").ToList();
        if (widgets.Count == 0) return null;

        var item = widgets[0];

        var topic       = ParseTopic(item);
        var title       = ParseTitle(item);
        var titleOrig   = item.Str("title") ?? "";
        var description = item.Str("synopsis");
        var date        = ParseBroadcastDate(item);
        var duration    = ParseDuration(item);
        var partner     = ParsePartner(item);
        var geo         = item.Path("mediaCollection", "embedded")?.Bool("isGeoBlocked") ?? false;

        if (topic is null || title is null || partner is null) return null;
        if (!ArdConstants.PartnerToKey.TryGetValue(partner, out var broadcasterKey)) return null;

        var streamsStd = ParseVideoUrls(item, "main",          "standard",          "video/mp4",                     "deu");
        var streamsHls = ParseVideoUrls(item, "main",          "standard",          "application/vnd.apple.mpegurl", "deu");
        var streamsAd  = ParseVideoUrls(item, "main",          "audio-description", "video/mp4",                     "deu");
        var streamsDgs = ParseVideoUrls(item, "sign-language", "standard",          "video/mp4",                     "deu");
        var streamsOv  = ParseVideoUrls(item, "main",          "standard",          "video/mp4",                     "*");

        // Funk fallback: no direct MP4 → fall back to HLS
        if (streamsStd.Count == 0 && streamsAd.Count == 0 && streamsDgs.Count == 0
            && streamsOv.Count == 0 && streamsHls.Count > 0)
        {
            streamsStd = streamsHls.Select(s => s with { IsHls = true }).ToList();
        }

        // Re-classify based on title suffixes
        if (titleOrig.Contains(" (mit Gebärdensprache)") || titleOrig.Contains(" mit Gebärdensprache"))
        {
            if (streamsStd.Count > 0 && streamsDgs.Count == 0)
            { streamsDgs = streamsStd; streamsStd = []; }
        }
        if (titleOrig.Contains("- Hörfassung") || titleOrig.Contains("(mit Audiodeskription)"))
        {
            if (streamsStd.Count > 0 && streamsAd.Count == 0)
            { streamsAd = streamsStd; streamsStd = []; }
        }

        var subtitleUrls = ParseSubtitleUrls(item);
        var relatedIds   = widgets.Count > 1 ? ParseRelatedIds(widgets[1]) : [];

        return new ArdEpisodeRaw(
            itemId, broadcasterKey, topic, title, description,
            date, duration, geo,
            streamsStd, streamsAd, streamsDgs, streamsOv,
            subtitleUrls, relatedIds);
    }

    private static string? ParseTopic(JsonElement item)
    {
        if (item.TryGetProperty("show", out var show) && show.ValueKind == JsonValueKind.Object)
        {
            var t = show.Str("title");
            if (t is not null)
            {
                if (t.Contains("MDR aktuell"))
                    t = Regex.Replace(t, @"[0-9][0-9]:[0-9][0-9] Uhr$", "").Trim();
                return t;
            }
        }
        return item.Str("title");
    }

    private static string? ParseTitle(JsonElement item)
    {
        var title = item.Str("title");
        if (title is null) return null;

        string[] noise =
        [
            " - Hörfassung", " (mit Gebärdensprache)", " mit Gebärdensprache",
            " (mit Audiodeskription)", "(Audiodeskription)", "Audiodeskription",
            " - (Originalversion)", " (OV)"
        ];
        foreach (var n in noise) title = title.Replace(n, "");
        return title.Trim();
    }

    private static DateTimeOffset? ParseBroadcastDate(JsonElement item)
    {
        var s = item.Str("broadcastedOn");
        if (s is null) return null;
        return DateTimeOffset.TryParse(s, out var dto) ? dto : null;
    }

    private static TimeSpan? ParseDuration(JsonElement item)
    {
        var embedded = item.Path("mediaCollection", "embedded");
        if (embedded is null) return null;
        var sec = embedded.Value.Path("meta")?.Long("duration")
               ?? embedded.Value.Path("meta")?.Long("durationSeconds");
        return sec.HasValue ? TimeSpan.FromSeconds(sec.Value) : null;
    }

    private static string? ParsePartner(JsonElement item)
    {
        if (!item.TryGetProperty("publicationService", out var ps)) return null;
        return ps.Str("partner") ?? ps.Str("name");
    }

    private static List<ArdStreamRaw> ParseVideoUrls(
        JsonElement item, string streamType, string audioType, string mimeType, string language)
    {
        var embedded = item.Path("mediaCollection", "embedded");
        if (embedded is null) return [];

        var results = new SortedDictionary<int, ArdStreamRaw>(
            Comparer<int>.Create((a, b) => b.CompareTo(a)));

        foreach (var streamCat in embedded.Value.Array("streams"))
        {
            if (!(streamCat.Str("kind") ?? "").Equals(streamType, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var video in streamCat.Array("media"))
            {
                if (!(video.Str("mimeType") ?? "").Equals(mimeType, StringComparison.OrdinalIgnoreCase))
                    continue;

                var audios = video.Array("audios").ToList();
                if (audios.Count == 0) continue;

                var audioKind = audios[0].Str("kind") ?? "";
                if (!audioKind.Equals(audioType, StringComparison.OrdinalIgnoreCase)) continue;

                var langCode = audios[0].Str("languageCode") ?? "";
                var langMatch = language == "*"
                    ? !langCode.Equals("deu", StringComparison.OrdinalIgnoreCase)
                    : langCode.Equals(language, StringComparison.OrdinalIgnoreCase);
                if (!langMatch) continue;

                var resStr = video.Str("maxHResolutionPx");
                var url    = video.Str("url");
                if (url is null || resStr is null) continue;
                if (!int.TryParse(resStr, out var res)) continue;

                var cleanUrl = url.Split('?')[0];
                var isHls    = mimeType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase);
                var quality  = MapResolution(res);

                results.TryAdd(res, new ArdStreamRaw(quality, cleanUrl, isHls));
            }
        }

        return NormalizeQualities([.. results.Values]);
    }

    private static List<ArdStreamRaw> NormalizeQualities(List<ArdStreamRaw> streams)
    {
        if (streams.Count == 0 || streams.Any(s => s.Quality == StreamQuality.Normal))
            return streams;

        var result = streams.ToList();
        var hdIdx  = result.FindIndex(s => s.Quality == StreamQuality.Hd);
        if (hdIdx >= 0)
        {
            result[hdIdx] = result[hdIdx] with { Quality = StreamQuality.Normal };
        }
        else
        {
            var sdIdx = result.FindIndex(s => s.Quality == StreamQuality.Sd);
            if (sdIdx >= 0)
                result[sdIdx] = result[sdIdx] with { Quality = StreamQuality.Normal };
        }
        return result;
    }

    private static IReadOnlyList<string> ParseSubtitleUrls(JsonElement item)
    {
        var embedded = item.Path("mediaCollection", "embedded");
        if (embedded is null) return [];

        var subtitles = embedded.Value.Array("subtitles").ToList();
        if (subtitles.Count == 0) return [];

        string? best = null;
        foreach (var src in subtitles[0].Array("sources"))
        {
            var url = src.Str("url");
            if (url is null) continue;
            if (!url.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase)) { best = url; break; }
            best ??= url;
        }
        return best is null ? [] : [best];
    }

    private static IReadOnlyList<string> ParseRelatedIds(JsonElement widget)
    {
        var ids = new List<string>();
        foreach (var teaser in widget.Array("teasers"))
        {
            var id = teaser.Str("id");
            if (id is not null) ids.Add(id);
        }
        return ids;
    }

    private static StreamQuality MapResolution(int width) => width switch
    {
        < 800  => StreamQuality.Sd,
        < 1600 => StreamQuality.Normal,
        < 2560 => StreamQuality.Hd,
        _      => StreamQuality.Uhd,
    };

    // ── HTTP helpers ──────────────────────────────────────────────────────────

    private async Task<string?> GetRawAsync(string url, CancellationToken ct)
    {
        try
        {
            var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                log.LogWarning("ARD HTTP {Status} for {Url}", (int)resp.StatusCode, url);
                return null;
            }
            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.LogError(ex, "ARD fetch error: {Url}", url);
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
