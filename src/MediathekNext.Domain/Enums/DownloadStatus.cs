namespace MediathekNext.Domain.Enums;

public enum DownloadStatus
{
    // ── Active states ─────────────────────────────────────────
    Queued      = 0,   // Job created, waiting for TickerQ to pick up
    Resolving   = 1,   // Phase 1: verifying URL, detecting stream type
    Downloading = 2,   // Phase 2: ffmpeg running
    Finalising  = 3,   // Phase 3: atomic move + metadata embed

    // ── Terminal success ──────────────────────────────────────
    Completed   = 10,

    // ── Terminal failures (distinct for meaningful UX) ────────
    Cancelled         = 20,  // User cancelled
    UrlUnavailable    = 21,  // Resolve exhausted retries (geo-block, expired, 403)
    DownloadFailed    = 22,  // Download exhausted retries (network, disk)
    FinaliseFailed    = 23,  // File downloaded but move/metadata failed
}
