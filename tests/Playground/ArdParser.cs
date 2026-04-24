using System.Text.Json;
using System.Text.RegularExpressions;
using Mediathek.Models;

namespace Mediathek.Crawlers.Ard;

/// <summary>
/// All ARD JSON parsing in one place.
/// Translated from:
///   ArdFilmDeserializer.java      - item detail page parsing
///   ArdDayPageDeserializer.java   - EPG/day page -> item IDs
///   ArdTopicsDeserializer.java    - A-Z topics -> compilation URLs
/// </summary>
internal static class ArdParser
{
    // ── Day page -> item IDs ──────────────────────────────────────────────────
    // From ArdDayPageDeserializer.java
    // Shape: { channels: [ { timeSlots: [ [ { links: { target: { urlId } } } ] ] } ] }

    public static List<string> ParseDayPage(JsonElement root)
    {
        var ids = new List<string>();

        foreach (var channel in root.Array("channels"))
        foreach (var timeSlot in channel.Array("timeSlots"))
        foreach (var entry in timeSlot.Array())   // timeSlots is array-of-arrays
        {
            var id = ExtractUrlId(entry);
            if (id is not null) ids.Add(id);
        }

        return ids;
    }

    // Mirrors ArdDayPageDeserializer.toId()
    private static string? ExtractUrlId(JsonElement entry)
    {
        // Prefer links.target.urlId
        var target = entry.Path("links", "target");
        if (target is not null)
        {
            var id = target.Value.Str("urlId");
            if (id is not null) return id;
        }
        return entry.Str("urlId");
    }

    // ── Topics A-Z -> compilation URLs ────────────────────────────────────────
    // From ArdTopicsDeserializer.java
    // Shape: { widgets: [ { links: { self: { id } } } ] }
    // Each widget self.id -> compilation URL for that sender

    public static List<string> ParseTopicsPage(JsonElement root, string sender)
    {
        var urls = new List<string>();

        foreach (var widget in root.Array("widgets"))
        {
            var selfId = widget.Path("links", "self")?.Str("id");
            if (selfId is null) continue;

            urls.Add(string.Format(
                ArdConstants.TopicsCompilationUrl,
                sender,
                selfId,
                ArdConstants.TopicsCompilationPageSize));
        }

        return urls;
    }

    // ── Item detail -> episode ─────────────────────────────────────────────────
    // From ArdFilmDeserializer.java
    // Shape: { widgets: [ { /* main item */ }, { teasers: [ { id } ] } ] }

    public record ItemResult(
        string? BroadcasterKey,
        string? Topic,
        string? Title,
        string? Description,
        DateTimeOffset? BroadcastTime,
        TimeSpan? Duration,
        bool GeoBlocked,
        List<StreamEntry> Streams,          // standard German
        List<StreamEntry> StreamsAd,        // audio description
        List<StreamEntry> StreamsDgs,       // sign language
        List<StreamEntry> StreamsOv,        // original version
        List<SubtitleEntry> Subtitles,
        List<string> RelatedItemIds         // for numberOfClips > 1
    );

    public static ItemResult? ParseItemPage(JsonElement root)
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

        // Parse the four stream variants (mirrors ArdFilmDeserializer)
        var streamsStd = ParseVideoUrls(item, "main",         "standard",         "video/mp4",                           "deu");
        var streamsHls = ParseVideoUrls(item, "main",         "standard",         "application/vnd.apple.mpegurl",       "deu");
        var streamsAd  = ParseVideoUrls(item, "main",         "audio-description","video/mp4",                           "deu");
        var streamsDgs = ParseVideoUrls(item, "sign-language","standard",         "video/mp4",                           "deu");
        var streamsOv  = ParseVideoUrls(item, "main",         "standard",         "video/mp4",                           "*");  // non-deu

        // Funk fallback: no direct MP4 -> fall back from HLS playlist
        if (streamsStd.Count == 0 && streamsAd.Count == 0 && streamsDgs.Count == 0
            && streamsOv.Count == 0 && streamsHls.Count > 0)
        {
            streamsStd = streamsHls.Select(s => s with { IsHls = true }).ToList();
        }

        // Sub-page heuristics from ArdFilmDeserializer (re-classify based on title)
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

        var subtitles  = ParseSubtitles(item);
        var relatedIds = widgets.Count > 1
            ? ParseRelatedIds(widgets[1])
            : [];

        return new ItemResult(
            broadcasterKey, topic, title, description,
            date, duration, geo,
            streamsStd, streamsAd, streamsDgs, streamsOv,
            subtitles, relatedIds);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Mirrors ArdFilmDeserializer.parseTopic()
    private static string? ParseTopic(JsonElement item)
    {
        if (item.TryGetProperty("show", out var show) && show.ValueKind == JsonValueKind.Object)
        {
            var t = show.Str("title");
            if (t is not null)
            {
                // Strip time from "MDR aktuell 20:00 Uhr" pattern
                if (t.Contains("MDR aktuell"))
                    t = Regex.Replace(t, @"[0-9][0-9]:[0-9][0-9] Uhr$", "").Trim();
                return t;
            }
        }
        return item.Str("title");
    }

    // Mirrors ArdFilmDeserializer.parseTitle()
    private static string? ParseTitle(JsonElement item)
    {
        var title = item.Str("title");
        if (title is null) return null;

        // Strip accessibility/language suffixes
        string[] noise =
        [
            " - Hörfassung", " (mit Gebärdensprache)", " mit Gebärdensprache",
            " (mit Audiodeskription)", "(Audiodeskription)", "Audiodeskription",
            " - (Originalversion)", " (OV)"
        ];
        foreach (var n in noise)
            title = title.Replace(n, "");
        return title.Trim();
    }

    // Mirrors ArdFilmDeserializer.parseDate() — input is ISO-8601 with timezone
    private static DateTimeOffset? ParseBroadcastDate(JsonElement item)
    {
        var s = item.Str("broadcastedOn");
        if (s is null) return null;
        return DateTimeOffset.TryParse(s, out var dto) ? dto : null;
    }

    // Mirrors ArdFilmDeserializer.parseDuration()
    // Path: mediaCollection.embedded.meta.duration  OR  .meta.durationSeconds
    private static TimeSpan? ParseDuration(JsonElement item)
    {
        var embedded = item.Path("mediaCollection", "embedded");
        if (embedded is null) return null;

        var sec = embedded.Value.Path("meta")?.Long("duration")
               ?? embedded.Value.Path("meta")?.Long("durationSeconds");
        return sec.HasValue ? TimeSpan.FromSeconds(sec.Value) : null;
    }

    // Mirrors ArdFilmDeserializer.parsePartner()
    // Path: publicationService.partner  OR  publicationService.name
    private static string? ParsePartner(JsonElement item)
    {
        if (!item.TryGetProperty("publicationService", out var ps)) return null;
        return ps.Str("partner") ?? ps.Str("name");
    }

    // Mirrors ArdFilmDeserializer.parseVideoUrls() + parseVideoUrlMap()
    // Path: mediaCollection.embedded.streams[] -> { kind, media: [ { mimeType, maxHResolutionPx, url, audios: [ { kind, languageCode } ] } ] }
    private static List<StreamEntry> ParseVideoUrls(
        JsonElement item, string streamType, string audioType, string mimeType, string language)
    {
        var embedded = item.Path("mediaCollection", "embedded");
        if (embedded is null) return [];

        var results = new SortedDictionary<int, StreamEntry>(Comparer<int>.Create((a, b) => b.CompareTo(a)));

        foreach (var streamCat in embedded.Value.Array("streams"))
        {
            var kind = streamCat.Str("kind") ?? "";
            if (!kind.Equals(streamType, StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var video in streamCat.Array("media"))
            {
                var mime = video.Str("mimeType") ?? "";
                if (!mime.Equals(mimeType, StringComparison.OrdinalIgnoreCase)) continue;

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

                // Strip query parameters (mirrors UrlUtils.removeParameters)
                var cleanUrl = url.Split('?')[0];
                var isHls    = mimeType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase);
                var quality  = MapArdResolution(res);

                results.TryAdd(res, new StreamEntry(quality, StreamLanguage.German, cleanUrl, isHls));
            }
        }

        return NormalizeQualities(results.Values.ToList());
    }

    // Mirrors ArdFilmDeserializer.parseVideoUrls() normalization:
    // If no NORMAL quality, promote HD to NORMAL; if still none, promote SD.
    private static List<StreamEntry> NormalizeQualities(List<StreamEntry> streams)
    {
        if (streams.Count == 0) return streams;
        if (streams.Any(s => s.Quality == StreamQuality.Normal)) return streams;

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

    // Mirrors ArdFilmDeserializer.prepareSubtitleUrl()
    // Path: mediaCollection.embedded.subtitles[0].sources[] -> { url }
    // Prefer non-.vtt
    private static List<SubtitleEntry> ParseSubtitles(JsonElement item)
    {
        var embedded = item.Path("mediaCollection", "embedded");
        if (embedded is null) return [];

        var subtitles = embedded.Value.Array("subtitles").ToList();
        if (subtitles.Count == 0) return [];

        var sources = subtitles[0].Array("sources");
        string? best = null;
        foreach (var src in sources)
        {
            var url = src.Str("url");
            if (url is null) continue;
            if (!url.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase))
            {
                best = url;
                break;
            }
            best ??= url;
        }

        return best is null ? [] : [new SubtitleEntry(StreamLanguage.German, best)];
    }

    // Related item IDs (mirrors ArdFilmDeserializer.parseRelatedFilms())
    private static List<string> ParseRelatedIds(JsonElement widget)
    {
        var ids = new List<string>();
        foreach (var teaser in widget.Array("teasers"))
        {
            var id = teaser.Str("id");
            if (id is not null) ids.Add(id);
        }
        return ids;
    }

    // Resolution width -> quality tier
    // Derived from ArdFilmDeserializer: uses Qualities.getResolutionFromWidth
    // ARD typically has: 640 (SD), 960/1280 (Normal), 1920 (HD)
    private static StreamQuality MapArdResolution(int width) => width switch
    {
        < 800  => StreamQuality.Sd,
        < 1600 => StreamQuality.Normal,
        < 2560 => StreamQuality.Hd,
        _      => StreamQuality.Uhd,
    };
}
