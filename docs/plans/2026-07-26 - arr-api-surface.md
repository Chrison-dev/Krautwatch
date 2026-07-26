# 2026-07-26 — The *arr API surface (Newznab + SABnzbd)

**Status:** in progress · **Milestone:** Indexer · **Implements:** DR-010 (arr-indexer direction)

Krautwatch's reason to exist (DR-010) is to be a Sonarr/Radarr **indexer + download client**. This is
the plan for that surface, split into small reviewable PRs.

## Layout decision — all HTTP APIs under `Presentation/Api/{Project}`

```
src/Presentation/Api/
├── PublicApi/            existing internal JSON API (see cleanup below) — TO BE RESOLVED
└── NewznabIndexerApi/    NEW public *arr-facing surface (Newznab XML + SABnzbd JSON)
```

Namespaces keep the `Api` grouping (Presentation is dropped, per DR-009 §2):
`Krautwatch.Api.NewznabIndexerApi`, etc.

## Sonarr model adoption (PR 1 — this increment)

The catalog lacked structured numbering, so Sonarr couldn't match anything but daily air-dates. We
adopt Sonarr's series model:

- **`SeriesType`** enum (Standard | Daily | Anime) on `Show` — selects the matching regime. Default
  **Daily** (most German public-TV content is dated).
- **`SeasonNumber` / `EpisodeNumber` / `AbsoluteEpisodeNumber`** (nullable) on `Episode`.
  `BroadcastDate` remains the air-date key for the Daily regime.
- `Show.TvdbId` (nullable) reserved for future TVDB matching; unset for now.

The crawlers auto-classify: `EpisodeNumbering.Parse(title)` extracts SxxEyy / `(S02/E52)` /
`Staffel X Folge Y` / `NxM`; when found, the episode carries the numbers and the show is upgraded to
`Standard`, otherwise it stays `Daily`. So extra 3 / heute-show → Daily; Die Biene Maja → Standard S02E52
— exactly what Sonarr expects. EF migration `AddSeriesModel` (existing rows default to `Daily`).

## Newznab indexer (PR 2)

New `Presentation/Api/NewznabIndexerApi` host + `Application/Indexing` slice (Queries only):

- `GET /api?t=caps` → capabilities XML (categories 5000 TV / 2000 Movies, supported search params).
- `GET /api?t=tvsearch|search&q=…` → RSS 2.0 + `newznab:` attrs; releases built from the catalog.
  Release title switches on `SeriesType`: `… S02E52 …` (Standard) vs `… 2026-07-10 …` (Daily).
- `GET /api?t=…` RSS feed for RSS-Sync.
- Each release's download link is an **opaque token** (`Episode.Id`, the stable Newznab GUID from PR 1).
- Auth: a single `apikey` on `AppSettings`. The host does **not** own migrations (PublicApi/migrator does);
  it only reads. AppHost adds it with `WithExternalHttpEndpoints()`.

## SABnzbd download client (PR 3)

Same host, SABnzbd JSON API (`mode=version|queue|history|addurl|addfile`). `addurl/addfile` decodes the
token → `StartDownloadCommand` (reuses the existing `Downloads` slice); `queue`/`history` project
`DownloadJob` state. Closes the round-trip: Newznab result → SABnzbd add → Downloader pulls the raw MP4.

## Internal-API cleanup (PR 4, dedicated)

The existing `Api` is the JSON backend for the legacy Blazor **browse** UI (catalog/downloads/settings/
system) — a pre-DR-010 remnant. Post-DR-010 the UI shrinks to configuration, and `Web` is Blazor
**Server** (can call Application in-process, no HTTP hop). Plan: **collapse the internal API into the Web
config UI and delete the `Api` project**, trimming `Web` to a config/activity surface; move EF-migration
ownership to a dedicated migrator (or AppHost). Kept behind its own PR because it's a deletion.
