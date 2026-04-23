using System.ComponentModel.DataAnnotations;

namespace MediathekNext.Infrastructure.Catalog.MediathekView;

public class MediathekViewOptions
{
    public const string SectionName = "MediathekView";

    /// <summary>
    /// URL of the filmliste XZ file.
    /// Active servers: verteiler1, verteiler3, verteiler5, verteiler6
    /// </summary>
    public string FilmlisteUrl { get; set; } =
        "http://verteiler1.mediathekview.de/Filmliste-akt.xz";

    /// <summary>Timeout for the filmliste download in seconds (~90MB compressed).</summary>
    public int DownloadTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Debug: cap the number of entries parsed from the filmliste.
    /// 0 = unlimited (production default).
    /// Set to e.g. 500 for fast local iteration without downloading the full catalog.
    /// Has no effect when the filmliste contains fewer entries than this cap.
    /// </summary>
    public int MaxEntries { get; set; } = 0;
}
