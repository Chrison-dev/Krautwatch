namespace MediathekNext.Infrastructure.Catalog.MediathekView;

/// <summary>
/// Raw entry parsed directly from the filmliste "X" array.
/// Field order matches the filmliste specification exactly.
/// Empty strings mean "same as previous entry" — see FilmlisteParser.
/// </summary>
public record FilmlisteEntry(
    string Channel,       // [0] Sender
    string Topic,         // [1] Thema (show name)
    string Title,         // [2] Titel (episode title)
    string Date,          // [3] Datum (dd.MM.yyyy)
    string Time,          // [4] Zeit (HH:mm:ss)
    string Duration,      // [5] Dauer (HH:mm:ss)
    string SizeMb,        // [6] Größe [MB]
    string Description,   // [7] Beschreibung
    string UrlSd,         // [8] Url (SD stream)
    string Website,       // [9] Website
    string UrlSubtitle,   // [10] Url Untertitel
    string UrlRtmp,       // [11] Url RTMP (legacy, ignore)
    string UrlHd,         // [12] Url HD (suffix or full URL)
    string UrlHdRtmp,     // [13] Url RTMP HD (legacy, ignore)
    string UrlSmall,      // [14] Url Klein (suffix or full URL)
    string UrlSmallRtmp,  // [15] Url RTMP Klein (legacy, ignore)
    string UrlHistory,    // [16] Url History (ignore)
    string Geo,           // [17] Geo restriction
    string IsNew          // [18] neu (true/false)
);
