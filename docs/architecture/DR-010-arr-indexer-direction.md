# DR-010 — Product Direction: Sonarr/Radarr Indexer

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-07-25 |
| **Deciders** | Christian |
| **Refines** | DR-001 (catalog provider abstraction) |

## Context

The original design (DR-001) framed the product as a **standalone Mediathek browser**: its own
Blazor catalog UI, backed by the MediathekView filmliste, where users search and download directly.

The direction has changed. Krautwatch is now a **Sonarr/Radarr companion** — an *arr **indexer +
download client** for German public TV, in the spirit of MediathekArr. The `*arr` apps drive it;
Krautwatch does not own the browsing experience.

## Decision

Krautwatch integrates with the `*arr` stack via the interfaces those apps already speak:

- **Indexer (Newznab):** `t=caps`, `t=search`, `t=tvsearch` + an RSS feed for RSS-Sync. This is what
  Sonarr / Prowlarr call. Backed by per-broadcaster crawlers (ARD/KiKA, ZDF) writing a normalized
  catalog.
- **Download client (SABnzbd-compatible):** queue / history / add-by-id, so Sonarr/Radarr treat
  Krautwatch as a usenet download client. Actual pulls are direct HTTP/HLS + ffmpeg, with subtitles.
- **Reach-back:** poll each configured Sonarr/Radarr instance for its `monitored` series list; that
  is the crawl work-list.

The **UI shrinks to configuration** (Sonarr/Radarr instances, connection test) — not a browse/search
surface.

### What carries over from DR-001

The **provider/port abstraction survives** — it is still correct that broadcaster access sits behind
a port in Domain with concrete clients in Infrastructure. What changes is the *consumer*: the catalog
now feeds the Newznab indexer instead of a first-party browse UI. MediathekView is de-emphasised in
favour of the direct ARD/ZDF APIs (see the Foundation/Indexer milestone issues).

## Consequences

- ✅ Fits an existing, battle-tested workflow (`*arr` RSS-Sync + download-client model).
- ✅ No first-party catalog UI to build/maintain — smaller surface.
- ✅ Broadcaster crawlers remain pluggable (per DR-001) — each is a vertical slice + agent (DR-009).
- ⚠️ Correctness bar is set by `*arr` expectations (stable Newznab GUIDs, recency ordering, SABnzbd
  queue semantics) rather than by our own UI.
