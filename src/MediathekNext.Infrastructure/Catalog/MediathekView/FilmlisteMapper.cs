using System.Security.Cryptography;
using MediathekNext.Domain.Enums;
using System.Text;
using MediathekNext.Domain.Entities;

namespace MediathekNext.Infrastructure.Catalog.MediathekView;

/// <summary>
/// Maps raw FilmlisteEntry records to domain entities.
/// Handles URL resolution, ID generation, and date parsing.
/// </summary>
public static class FilmlisteMapper
{
    // Known channel IDs used in our domain
    private static readonly Dictionary<string, string> ChannelIdMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ARD"] = "ard",
        ["ZDF"] = "zdf"
    };

    public static (Episode? Episode, Channel Channel, Show Show) ToEpisode(FilmlisteEntry entry)
    {
        // Parse broadcast date + time
        var broadcastDate = ParseBroadcastDate(entry.Date, entry.Time);
        if (broadcastDate is null)
            return (null, null!, null!);

        // Parse duration
        var duration = ParseDuration(entry.Duration);

        // Resolve channel
        var channelId = ChannelIdMap.GetValueOrDefault(entry.Channel, entry.Channel.ToLower());
        var channel = new Channel
        {
            Id = channelId,
            Name = entry.Channel,
            ProviderKey = "mediathekview"
        };

        // Show = Topic field
        var showId = GenerateShowId(channelId, entry.Topic);
        var show = new Show
        {
            Id = showId,
            Title = entry.Topic,
            ChannelId = channelId,
            Channel = channel
        };

        // Resolve stream URLs
        var streams = ResolveStreams(entry);

        // Generate stable episode ID from content hash (see DR-001 notes)
        var episodeId = GenerateEpisodeId(entry);

        var episode = new Episode
        {
            Id = episodeId,
            Title = string.IsNullOrWhiteSpace(entry.Title) ? entry.Topic : entry.Title,
            Description = string.IsNullOrWhiteSpace(entry.Description) ? null : entry.Description,
            ShowId = showId,
            Show = show,
            BroadcastDate = broadcastDate.Value,
            Duration = duration,
            ContentType = InferContentType(entry.Topic),
            Streams = streams
        };

        return (episode, channel, show);
    }

    private static List<EpisodeStream> ResolveStreams(FilmlisteEntry entry)
    {
        var streams = new List<EpisodeStream>();
        var baseSdUrl = entry.UrlSd;

        if (!string.IsNullOrWhiteSpace(baseSdUrl))
        {
            var sdId = GenerateStreamId(baseSdUrl, VideoQuality.Standard);
            streams.Add(new EpisodeStream
            {
                Id = sdId,
                EpisodeId = "", // set after episode ID is known
                Quality = VideoQuality.Standard,
                Url = baseSdUrl,
                Format = InferFormat(baseSdUrl)
            });
        }

        // HD URL may be a full URL or a suffix appended to the SD URL
        var hdUrl = ResolveAlternateUrl(baseSdUrl, entry.UrlHd);
        if (!string.IsNullOrWhiteSpace(hdUrl))
        {
            streams.Add(new EpisodeStream
            {
                Id = GenerateStreamId(hdUrl, VideoQuality.High),
                EpisodeId = "",
                Quality = VideoQuality.High,
                Url = hdUrl,
                Format = InferFormat(hdUrl)
            });
        }

        // Small URL — same pattern
        var smallUrl = ResolveAlternateUrl(baseSdUrl, entry.UrlSmall);
        if (!string.IsNullOrWhiteSpace(smallUrl))
        {
            streams.Add(new EpisodeStream
            {
                Id = GenerateStreamId(smallUrl, VideoQuality.Low),
                EpisodeId = "",
                Quality = VideoQuality.Low,
                Url = smallUrl,
                Format = InferFormat(smallUrl)
            });
        }

        return streams;
    }

    /// <summary>
    /// Filmliste alternate URLs are encoded as:
    ///   - Full URL: starts with "http" → use as-is
    ///   - Suffix only: "|suffix" → replace last segment of base URL with suffix
    /// </summary>
    private static string? ResolveAlternateUrl(string baseUrl, string alternate)
    {
        if (string.IsNullOrWhiteSpace(alternate))
            return null;

        if (alternate.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return alternate;

        // Format: "NNN|suffix" where NNN is the number of chars to strip from base
        if (alternate.Contains('|'))
        {
            var parts = alternate.Split('|', 2);
            if (int.TryParse(parts[0], out var stripCount) && stripCount <= baseUrl.Length)
                return baseUrl[..^stripCount] + parts[1];
        }

        return null;
    }

    private static string InferFormat(string url)
    {
        if (url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)) return "m3u8";
        if (url.Contains(".mp4", StringComparison.OrdinalIgnoreCase)) return "mp4";
        if (url.Contains(".webm", StringComparison.OrdinalIgnoreCase)) return "webm";
        return "mp4"; // sensible default
    }

    private static DateTimeOffset? ParseBroadcastDate(string date, string time)
    {
        if (string.IsNullOrWhiteSpace(date))
            return null;

        var dateTimeStr = string.IsNullOrWhiteSpace(time)
            ? $"{date} 00:00:00"
            : $"{date} {time}";

        if (DateTime.TryParseExact(
                dateTimeStr,
                "dd.MM.yyyy HH:mm:ss",
                global::System.Globalization.CultureInfo.InvariantCulture,
                global::System.Globalization.DateTimeStyles.None,
                out var dt))
        {
            // German public TV broadcasts are in CET/CEST
            return new DateTimeOffset(dt, TimeSpan.FromHours(1));
        }

        return null;
    }

    private static TimeSpan ParseDuration(string duration)
    {
        if (string.IsNullOrWhiteSpace(duration))
            return TimeSpan.Zero;

        return TimeSpan.TryParseExact(duration, @"hh\:mm\:ss", null, out var ts)
            ? ts
            : TimeSpan.Zero;
    }

    /// <summary>
    /// Generates a stable show ID from channel + topic.
    /// </summary>
    private static string GenerateShowId(string channelId, string topic)
    {
        var input = $"{channelId}_{topic}";
        return $"show_{HashShort(input)}";
    }

    /// <summary>
    /// Generates a stable episode ID by hashing key content fields.
    /// Mirrors the approach used by MediathekViewWeb.
    /// </summary>
    private static string GenerateEpisodeId(FilmlisteEntry entry)
    {
        var input = string.Join("_", entry.Channel, entry.Topic, entry.Title,
            entry.Date, entry.Time, entry.UrlSd, entry.UrlHd);
        return $"ep_{HashShort(input)}";
    }

    private static string GenerateStreamId(string url, VideoQuality quality) =>
        $"stream_{HashShort($"{url}_{quality}")}";

    private static string HashShort(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes)[..16].ToLower();
    }
    /// <summary>
    /// Infers ContentType from the Topic (Thema) field using keyword heuristics.
    /// This is imperfect but practical — ZDF API will provide explicit types later.
    /// </summary>
    private static ContentType InferContentType(string topic)
    {
        if (string.IsNullOrWhiteSpace(topic))
            return ContentType.Episode;

        var t = topic.ToLower();

        if (t.Contains("film") || t.Contains("spielfilm") || t.Contains("kino") ||
            t.Contains("thriller") || t.Contains("krimi") || t.Contains("komödie"))
            return ContentType.Movie;

        if (t.Contains("doku") || t.Contains("dokumentation") || t.Contains("reportage"))
            return ContentType.Documentary;

        return ContentType.Episode;
    }

}
