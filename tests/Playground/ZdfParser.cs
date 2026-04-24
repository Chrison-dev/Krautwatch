using System.Text.Json;
using Mediathek.Models;

namespace Mediathek.Crawlers.Zdf;

/// <summary>
/// All ZDF JSON parsing logic in one place.
/// Translated directly from:
///   ZdfTopicBaseClass.java
///   ZdfLetterPageDeserializer.java
///   ZdfTopicSeasonDeserializer.java
///   ZdfDownloadDtoDeserializer.java
/// </summary>
internal static class ZdfParser
{
    // ── Letter page -> topic refs ─────────────────────────────────────────────
    // Path: data.specialPageByCanonical.content.nodes[]
    // Each node is a show with title, canonical, countSeasons, contentOwner.title

    public record TopicRef(string Topic, string Canonical, int SeasonCount, bool HasNoSeason, string? Id);

    public static (List<TopicRef> Topics, string? NextCursor) ParseLetterPage(JsonElement root)
    {
        var topics = new List<TopicRef>();

        var nodes = root.Path("data", "specialPageByCanonical", "content", "nodes").Array();
        foreach (var node in nodes)
        {
            // Filter to known ZDF senders only
            var sender = node.Path("contentOwner")?.Str("title") ?? "ZDF";
            if (!ZdfConstants.PartnerToKey.ContainsKey(sender)) continue;

            var topic        = node.Str("title") ?? "";
            var countSeasons = node.Str("countSeasons");
            var canonical    = node.Str("canonical");
            var id           = node.Str("id");

            if (countSeasons is null)
            {
                // No season structure — use collection ID directly (terra Xplore etc.)
                if (id is not null)
                    topics.Add(new TopicRef(topic, id, 0, HasNoSeason: true, Id: id));
            }
            else if (canonical is not null && int.TryParse(countSeasons, out var sc))
            {
                for (int i = 0; i < sc; i++)
                    topics.Add(new TopicRef(topic, canonical, sc, HasNoSeason: false, Id: null));
            }
        }

        var pageInfo   = root.Path("data", "specialPageByCanonical", "content", "pageInfo");
        var nextCursor = ParseNextCursor(pageInfo);
        return (topics, nextCursor);
    }

    // ── Topic season -> episode download URLs ────────────────────────────────
    // Path: data.smartCollectionByCanonical.seasons.nodes[].episodes.nodes[]
    // or:   data.metaCollectionContent.smartCollections[]

    public record EpisodeRef(
        string Topic,
        string BroadcasterKey,
        string Title,
        string? Description,
        string? Website,
        DateTime? Time,
        Dictionary<string, string> DownloadUrlsByType  // vodMediaType -> ptmdTemplate URL
    );

    public static (List<EpisodeRef> Episodes, string? NextCursor) ParseTopicSeason(
        JsonElement root, string topic)
    {
        var episodes = new List<EpisodeRef>();
        string? nextCursor = null;

        var data = root.Path("data");
        if (data is null) return (episodes, nextCursor);

        if (data.Value.TryGetProperty("smartCollectionByCanonical", out var scc))
        {
            var seasonNodes = scc.Path("seasons", "nodes").Array();
            foreach (var season in seasonNodes)
            {
                var eps      = season.Path("episodes");
                var nodes    = eps.Path("nodes").Array();
                foreach (var node in nodes)
                    ParseEpisodeNode(node, topic, episodes);

                nextCursor = ParseNextCursor(eps.Path("pageInfo"));
            }
        }
        else if (data.Value.TryGetProperty("metaCollectionContent", out var mcc))
        {
            var nodes = mcc.Array("smartCollections");
            foreach (var node in nodes)
                ParseEpisodeNode(node, topic, episodes);

            nextCursor = ParseNextCursor(mcc.Path("pageInfo"));
        }

        return (episodes, nextCursor);
    }

    // Mirrors ZdfTopicBaseClass.deserializeMovie()
    private static void ParseEpisodeNode(JsonElement node, string topic, List<EpisodeRef> out_)
    {
        var title       = ParseEpisodeTitle(node);
        var website     = node.Str("sharingUrl");
        var time        = ParseEditorialDate(node);
        var description = node.Path("teaser")?.Str("description");
        var sender      = ParseSender(node);

        if (title is null) return;

        if (!ZdfConstants.PartnerToKey.TryGetValue(sender ?? "EMPTY", out var senderKey)) return;

        // Collect download URLs by vodMediaType (maps to language/type variant)
        // Path: video.currentMedia.nodes[] -> { vodMediaType, ptmdTemplate }
        var downloadUrls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var videoRoot  = node.TryGetProperty("video", out var vEl) && !vEl.Equals(default) ? vEl : node;
        var mediaNodes = videoRoot.Path("currentMedia", "nodes").Array();
        foreach (var media in mediaNodes)
        {
            var mediaType = media.Str("vodMediaType");
            var url       = media.Str("ptmdTemplate");
            if (mediaType is not null && url is not null)
                downloadUrls[mediaType] = FinalizeUrl(url);
        }

        if (downloadUrls.Count == 0) return;

        out_.Add(new EpisodeRef(topic, senderKey, title, description, website, time, downloadUrls));
    }

    // ── Download DTO -> streams ───────────────────────────────────────────────
    // Mirrors ZdfDownloadDtoDeserializer.java
    // JSON shape (from download endpoint):
    //   { attributes: { duration: { value: ms }, geoLocation: { value: "de" } },
    //     priorityList: [ { formitaeten: [ { mimeType, qualities: [ { quality, highestVerticalResolution, audio: { tracks: [ { class, language, uri } ] } } ] } ] } ],
    //     captions: [ { uri, language } ] }

    public record DownloadResult(
        TimeSpan? Duration,
        GeoRestriction Geo,
        List<StreamEntry> Streams,
        List<SubtitleEntry> Subtitles
    );

    public static DownloadResult? ParseDownload(JsonElement root)
    {
        var duration = ParseDownloadDuration(root);
        var geo      = ParseGeoLocation(root);
        var streams  = ParseVideoUrls(root);
        var subs     = ParseSubtitles(root);

        return new DownloadResult(duration, geo, streams, subs);
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
            "DE"    => GeoRestriction.De,
            "AT"    => GeoRestriction.At,
            "CH"    => GeoRestriction.Ch,
            "DACH"  => GeoRestriction.Dach,
            "WELT"  => GeoRestriction.World,
            _       => GeoRestriction.None,
        };
    }

    private static List<StreamEntry> ParseVideoUrls(JsonElement root)
    {
        // Collect all tracks with resolution, sort ascending, then map to quality enum.
        // Mirrors: parsePriority -> parseFormitaet -> extractTrack logic.
        var downloads = new List<(string Language, string Uri, int VertRes, StreamQuality Quality)>();

        foreach (var priority in root.Array("priorityList"))
        foreach (var formitaet in priority.Array("formitaeten"))
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

                    // Audio description suffix (from extractTrack)
                    if (string.Equals(cls, "ad", StringComparison.OrdinalIgnoreCase))
                        lang += "-ad";

                    downloads.Add((lang, uri, vertRes, q));
                }
            }
        }

        // Sort by resolution ascending (lowest first — mirrors Java's sort)
        downloads.Sort((a, b) => a.VertRes.CompareTo(b.VertRes));

        return downloads
            .Select(d => new StreamEntry(d.Quality, MapZdfLanguage(d.Language), d.Uri, IsHls: false))
            .ToList();
    }

    private static List<SubtitleEntry> ParseSubtitles(JsonElement root)
    {
        var subs = new List<SubtitleEntry>();
        // Prefer .xml subtitles over others (mirrors ZdfDownloadDtoDeserializer)
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
                // Remove existing non-xml entry if we have a better one
                if (seen.ContainsKey(langEnum))
                    subs.RemoveAll(s => s.Language == langEnum);

                subs.Add(new SubtitleEntry(langEnum, uri));
                seen[langEnum] = isXml;
            }
        }
        return subs;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Mirrors ZdfTopicBaseClass.parseTitle() + formatTitle() + formatEpisodeTitle()
    private static string? ParseEpisodeTitle(JsonElement node)
    {
        var title    = node.Str("title");
        var subtitle = node.Str("subtitle");
        if (title is null) return null;

        var result = subtitle is { Length: > 0 }
            ? $"{title.Trim()} - {subtitle.Trim()}"
            : title.Trim();

        // Append S01/E02 if episode info present
        if (node.TryGetProperty("episodeInfo", out var epInfo))
        {
            var season  = epInfo.Int("seasonNumber");
            var episode = epInfo.Int("episodeNumber");
            if (season.HasValue || episode.HasValue)
            {
                var tag = "";
                if (season.HasValue)  tag += $"S{season:D2}";
                if (season.HasValue && episode.HasValue) tag += "/";
                if (episode.HasValue) tag += $"E{episode:D2}";
                result = $"{result} ({tag})".Trim();
            }
        }

        // Strip CC noise (mirrors cleanupTitle)
        return System.Text.RegularExpressions.Regex.Replace(
            result, @"\(CC.*\) - .* Creative Commons.*", "").Trim();
    }

    // Mirrors ZdfTopicBaseClass.parseSender()
    private static string? ParseSender(JsonElement node)
    {
        if (node.TryGetProperty("contentOwner", out var co) && co.ValueKind == JsonValueKind.Object)
        {
            var details = co.Str("details");
            if (details is not null) return details;
        }

        var av = node.Path("tracking", "piano", "video");
        if (av is null) return null;

        return av.Value.Str("av_broadcastdetail")
            ?? av.Value.Str("av_broadcaster");
    }

    // Mirrors ZdfTopicBaseClass.parseDate()
    private static DateTime? ParseEditorialDate(JsonElement node)
    {
        var s = node.Str("editorialDate");
        if (s is null) return null;
        return DateTimeOffset.TryParse(s, out var dto) ? dto.LocalDateTime : null;
    }

    // Mirrors ZdfTopicBaseClass.finalizeDownloadUrl()
    private static string FinalizeUrl(string url)
    {
        // Ensure absolute URL
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            url = ZdfConstants.ApiBase + url;

        // Replace {playerId} placeholder
        return url.Replace("{playerId}", "android_native_5");
    }

    private static string? ParseNextCursor(JsonElement? pageInfo)
    {
        if (pageInfo is null) return null;
        var hasNext = pageInfo.Value.Str("hasNextPage");
        if (hasNext != "true") return null;
        return pageInfo.Value.Str("endCursor");
    }

    // From ZdfDownloadDtoDeserializer: quality string -> enum
    private static StreamQuality MapZdfQuality(string q) => q.ToLowerInvariant() switch
    {
        "low" or "med" or "medium" or "high" => StreamQuality.Sd,
        "veryhigh" or "hd"                   => StreamQuality.Normal,
        "fhd"                                => StreamQuality.Hd,
        "uhd"                                => StreamQuality.Uhd,
        _                                    => StreamQuality.Sd,
    };

    // Language string -> enum
    public static StreamLanguage MapZdfLanguage(string lang) =>
        lang.ToLowerInvariant() switch
        {
            "deu"     => StreamLanguage.German,
            "deu-ad"  => StreamLanguage.GermanAd,
            "deu-dgs" => StreamLanguage.GermanDgs,
            "eng"     => StreamLanguage.English,
            "fra"     => StreamLanguage.French,
            _         => StreamLanguage.Other,
        };
}
