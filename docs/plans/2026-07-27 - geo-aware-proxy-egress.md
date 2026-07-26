# Geo-aware proxy egress for DACH-restricted downloads (#45)

Implements the outcome of the #41 investigation: some public-TV assets (KiKA / licensed
cartoons like *Die Biene Maja*) are **geo-restricted to DACH** and enforced **purely by
country-of-egress** (proven live — a German IP returns the full stream; `geoLocation: "dach"`
is declared by ZDF's own API). The operator is a GEZ payer with legitimate access, so the fix
is to route the *fetch* through a German egress — but only for the content that needs it.

## Shape (three independently-reviewable PRs)

### PR 1 — Detection (this PR) · `feat/geo-detection`
Surface the broadcaster's own geo flag through the model. **No behaviour change** — the flag is
recorded but nothing routes on it yet.

- `EpisodeDetail.GeoRestricted` (Infrastructure crawl contract).
- Read it at resolve time:
  - **ARD** page-gateway → `mediaCollection.embedded.isGeoBlocked` (bool).
  - **ZDF** PTMD → `attributes.geoLocation.value` — restricted when `!= "none"` (e.g. `"dach"`, `"de"`).
- Map → `Episode.GeoRestricted` (Domain, persisted) via `EpisodeMapper`.
- Snapshot → `DownloadJob.GeoRestricted` at enqueue (`AddDownloadByTokenHandler`), alongside the
  existing `StreamUrl`/`Quality` snapshot, so the download path is self-contained.
- One EF migration (`Episode.GeoRestricted`, `DownloadJob.GeoRestricted`).

### PR 2 — Mode A: bring-your-own proxy + geo-aware routing · `feat/geo-proxy-routing`
- `Download:ProxyUrl` setting (config; exposed in Settings + standalone UI). Empty = off (default).
- Downloader holds a **second, proxied `HttpClient`** (`WebProxy`), separate from the direct one
  (same UA + infinite timeout). Providers pick it **only when `job.GeoRestricted && ProxyUrl set`**.
- Geo-restricted job + no proxy configured → fail fast with a clear, actionable message.
- `FullDownloadTests` KiKA case drops `tolerateGeoBlock` when run through a DE egress.

### PR 3 — Mode B: auto-sourced public proxy list · `feat/geo-proxy-autolist`
- `Proxy` DB table (source metrics + our own probe results) + repository + EF migration.
- `ProxyRefreshService : BackgroundService` (mirrors `CrawlSchedulerService`) refreshes from a public
  list (GeoNode) on a configurable interval (default once/day). Config `Download:ProxyList:*`.
- Selection at download time reads the table: rank best-first (uptime × recency × speed), prefer
  probed-OK + verified-DE rows, verify egress + health-probe, fall through on failure.
- Opt-in; Mode A stays the recommended default. Open proxies are best-effort/untrusted — integrity
  is gated by the existing `ftyp` + size check; no credentials ever transit (public content only).

See the #45 thread for the full design discussion (ranking fields, selection algorithm, caveats).
