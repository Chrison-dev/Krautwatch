namespace Mediathek.Models;

// ── Enums ─────────────────────────────────────────────────────────────────────

public enum StreamQuality  { Sd, Normal, Hd, Uhd }
public enum StreamLanguage { German, GermanAd, GermanDgs, English, French, Original, Other }
public enum GeoRestriction { None, De, At, Ch, Dach, World }

// ── Broadcaster ───────────────────────────────────────────────────────────────
// One row per station: ZDF, ZDFneo, ARD, BR, NDR, etc.

public class Broadcaster
{
    public int    Id          { get; set; }
    public string Key         { get; set; } = null!;   // "ZDF", "ARD", "BR" …
    public string DisplayName { get; set; } = null!;   // "ZDF", "Das Erste" …

    public ICollection<Show> Shows { get; set; } = [];
}

// ── Show ──────────────────────────────────────────────────────────────────────
// A series / Sendung: "Tatort", "heute journal", "Lindenstraße"

public class Show
{
    public int    Id           { get; set; }
    public int    BroadcasterId{ get; set; }
    public Broadcaster Broadcaster { get; set; } = null!;

    public string  Title       { get; set; } = null!;  // "Tatort"
    public string? ExternalId  { get; set; }            // ZDF canonical / ARD topic ID

    public ICollection<Episode> Episodes { get; set; } = [];
}

// ── Episode ───────────────────────────────────────────────────────────────────
// One broadcast of a show: "Tatort – Mord im Rathaus vom 03.04.2025"

public class Episode
{
    public int    Id          { get; set; }
    public int    ShowId      { get; set; }
    public Show   Show        { get; set; } = null!;

    public string  Title       { get; set; } = null!;
    public string? Description { get; set; }
    public DateOnly?       AiredOn  { get; set; }
    public TimeOnly?       AiredAt  { get; set; }
    public TimeSpan?       Duration { get; set; }
    public string?         WebsiteUrl   { get; set; }
    public string?         ThumbnailUrl { get; set; }
    public GeoRestriction  Geo          { get; set; } = GeoRestriction.None;
    public string?         ExternalId   { get; set; }  // broadcaster's own ID / canonical
    public DateTimeOffset  ImportedAt   { get; set; }

    public ICollection<EpisodeStream>   Streams   { get; set; } = [];
    public ICollection<EpisodeSubtitle> Subtitles { get; set; } = [];
}

// ── EpisodeStream ─────────────────────────────────────────────────────────────
// One quality+language variant of an episode.
// e.g. HD German, SD GermanAD, Normal GermanDGS …

public class EpisodeStream
{
    public int     Id        { get; set; }
    public int     EpisodeId { get; set; }
    public Episode Episode   { get; set; } = null!;

    public StreamQuality  Quality  { get; set; }
    public StreamLanguage Language { get; set; }
    public string         Url      { get; set; } = null!;
    public bool           IsHls    { get; set; }   // .m3u8 playlist vs direct mp4
}

// ── EpisodeSubtitle ───────────────────────────────────────────────────────────

public class EpisodeSubtitle
{
    public int     Id        { get; set; }
    public int     EpisodeId { get; set; }
    public Episode Episode   { get; set; } = null!;

    public StreamLanguage Language { get; set; }
    public string         Url      { get; set; } = null!;
}
