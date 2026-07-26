# 2026-07-26 — Wire the crawl Actions into the agents

**Status:** in progress · **Milestone:** Foundation / Indexer · **Implements:** DR-009, DR-010

## Why this is a plan, not an ADR

Nothing new is *decided* here — DR-009 already fixed the hexagon, the `Application/Crawling`
slice, "broadcaster access sits behind a Domain port with concrete clients in Infrastructure"
(DR-010), and "Application only sees message contracts + a dispatch port; Infrastructure wires
the transport" (DR-009 §5). This is the faithful **implementation** of those decisions. The one
refinement worth flagging for review is recorded below (single generic Action vs. per-broadcaster
Action files) — it stays inside DR-009's envelope, so it's a note here rather than a new DR.

## Goal

The agent fleet is scaffolded and wired to Postgres + durable Wolverine (increment #5), but the
hosts still carry `TODO (#3)` — they don't crawl anything. This increment makes the **ARD (+KiKA)**
and **ZDF** agents actually crawl their configured shows into Postgres, so the catalog becomes
queryable through the existing `SearchCatalog`.

**In scope:** the crawl wiring only. **Out of scope (follow-on PRs):** the Newznab/SABnzbd API
surface, and the Downloader agent's real ffmpeg pull. This keeps the PR small and reviewable.

## The seam (DR-009 hexagon)

The crawler clients (`ArdCatalogClient`, `ZdfCatalogClient`) live in **Infrastructure** and return
an Infrastructure type (`EpisodeDetail`). The Action lives in **Application**, which may depend on
**Domain only** (enforced by ArchUnitNET). So we introduce the missing Domain port and adapt the
clients behind it:

```
Domain/Interfaces/IBroadcasterCrawler.cs      NEW port
    string ProviderKey { get; }                  // "ard" | "kika" | "zdf"
    Task<IReadOnlyList<Episode>> CrawlShowAsync(string showQuery, CancellationToken ct);
Domain/Interfaces/IMessageDispatcher.cs       NEW dispatch port (DR-009 §5)
    Task PublishAsync(object message, CancellationToken ct);

Infrastructure/Crawling/EpisodeMapper.cs      NEW  EpisodeDetail → Domain Episode (deterministic IDs)
Infrastructure/Crawling/Ard/ArdBroadcasterCrawler.cs   NEW  wraps ArdCatalogClient (ard + kika scopes)
Infrastructure/Crawling/Zdf/ZdfBroadcasterCrawler.cs   NEW  wraps ZdfCatalogClient
Infrastructure/Messaging/WolverineDispatcher.cs        NEW  IMessageDispatcher → Wolverine IMessageBus

Application/Crawling/CrawlShow.cs             NEW  CrawlShowCommand + CrawlShowHandler (the Action)
Application/Crawling/CrawlScheduler.cs        NEW  CrawlOptions + CrawlSchedulerService (BackgroundService)
```

The `EpisodeDetail → Episode` mapping lives **inside the adapter** — the only place allowed to see
both types — which keeps the arch tests green. IDs are derived from the broadcaster's **native id**
(ARD `ArdEpisode.Id`, ZDF `canonical`) so re-crawls upsert instead of duplicating.

## Flow (per agent)

```
CrawlSchedulerService (Application, BackgroundService)
    │  on startup + every CrawlOptions.Interval, for each configured (ProviderKey, ShowQuery)
    ▼  IMessageDispatcher.PublishAsync(new CrawlShowCommand("ard", "Extra 3"))
WolverineDispatcher (Infra)  →  IMessageBus.PublishAsync  [durable local queue, Postgres]
    ▼
CrawlShowHandler (Application, Wolverine-discovered)
    │  picks IBroadcasterCrawler by ProviderKey  →  CrawlShowAsync(showQuery)   [external IO = Action]
    ▼  IEpisodeRepository.UpsertManyAsync(episodes)                              [persist]
Postgres → queryable via SearchCatalog
```

The scheduler dispatches through the port (Application never sees Wolverine). The handler takes no
Wolverine types in its signature — Wolverine discovers it by convention; we point its discovery at
the Application assembly in each agent host.

## Seed = configured show-list

`CrawlOptions` is bound from config per agent, seeded with the three shows already proven live in
PR #34: **Extra 3** (ard), **Die Biene Maja** (kika), **heute-show** (zdf). A proper watchlist driven
by each Sonarr instance's `monitored` list (DR-010) only makes sense once Newznab exists, so it's
deferred to a later increment.

## Refinement flagged for review

DR-009 sketches per-broadcaster Action files (`Action/ArdCrawling.cs`, `ZdfCrawling.cs`). Because
the `IBroadcasterCrawler` port fully encapsulates each broadcaster's *workflow* in its Infrastructure
adapter, the Application Action has nothing broadcaster-specific left — so we keep **one** generic
`CrawlShowHandler` that resolves the crawler by `ProviderKey`, matching the existing flat-slice file
idiom (`SearchCatalog.cs`, `DownloadHandlers.cs`). "A broadcaster is its own slice" is honoured at
the **adapter + agent host + config** layer: adding one is still a new adapter + host wiring + seed
entry, exactly as CLAUDE.md promises. No sibling-slice coupling is introduced.

## Persistence fix (needed)

`EpisodeRepository.UpsertManyAsync` only checked *Episode* existence, so a crawl producing fresh
`Channel`/`Show`/`Stream` graphs would fail (duplicate-key on insert / update-zero-rows). The upsert
is reworked to dedupe distinct channels/shows from the batch graph and set entity state by existence
before upserting episodes. The existing `UpsertManyAsync_NewEpisodes_AreInserted` test still passes.

## Tests

- **Architecture:** add `Crawling` to the slice-isolation `[Theory]` (must not depend on siblings).
- **Infrastructure:** `EpisodeMapper` mapping (deterministic ids, stream populated, synopsis→description);
  graph-safe upsert inserts a brand-new channel/show/stream graph.
- **Application:** `CrawlShowHandler` with a fake `IBroadcasterCrawler` + in-memory sqlite repo →
  episodes land and are queryable; unknown ProviderKey is a no-op.
- **Live (opt-in, off CI):** ARD crawler adapter end-to-end (`Extra 3` → ≥1 Episode with a stream url).

CI stays `./build.cmd Test` (deterministic); live via `./build.cmd TestLive`.
