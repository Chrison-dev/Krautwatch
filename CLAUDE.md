# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this project is

**Krautwatch** is a self-hosted **Sonarr/Radarr indexer + download client** for German public TV
(ARD, ZDF, KiKA, …), in the spirit of MediathekArr. The `*arr` apps drive it:

- It exposes a **Newznab indexer** (`caps`/`search`/`tvsearch` + an RSS feed) that Sonarr/Prowlarr call.
- It exposes a **SABnzbd-compatible download client** API, then pulls the actual streams via direct
  HTTP/HLS + ffmpeg (with subtitles).
- Per-broadcaster **crawler agents** build the catalog; the only first-party UI is **instance
  configuration** (which Sonarr/Radarr to reach back to). See DR-010.

> **⚠️ Architecture reset in progress (DR-009).** The solution has been renamed
> **MediathekNext → Krautwatch** and reshaped into the layers below (Presentation fleet in place;
> `CoreWorker`/`Worker` role hosts dropped). Postgres + durable Wolverine (Postgres transport) are wired and a
> run-to-completion **Migrator** owns EF migrations. The **ARD (+KiKA) and ZDF agents crawl their configured shows into Postgres** via the
> `Application/Crawling` Action (broadcaster clients behind the `IBroadcasterCrawler` port; the
> scheduler dispatches `CrawlShowCommand` through the `IMessageDispatcher` port over the durable bus).
> The **Newznab indexer + SABnzbd download client** are live (`Api/NewznabIndexerApi`); the pre-DR-010
> browser (`Api/PublicApi` + `Web`) is retired. The **Downloader agent** polls the durable job table and
> pulls each stream to disk, routing by type: a raw byte copy for progressive MP4, or an ffmpeg remux
> (`-c copy`) for HLS. The superseded `docker/` topology (DR-004) is dead and will be replaced by
> Aspire-generated compose in the distribution milestone.
>
> **Known gaps (don't assume these exist):**
> - **Search is not query-driven yet, and the crawl list is hardcoded.** `CrawlOptions.Targets` binds
>   from each agent's `Crawl` config section and falls back to seed shows in `Agents/{Ard,Zdf}/Program.cs`
>   (`extra 3` / `Die Biene Maja` / `heute-show`). So `t=tvsearch` only finds what has already been
>   crawled — an unseeded show returns nothing. **DR-011** makes on-demand resolution the target and
>   demotes the standing list to RSS-feed input; reach-back to Sonarr is an optional pre-warm, not a
>   requirement (DR-010's work-list clause is retracted). No Sonarr/Radarr-instance config UI yet either
>   — `Settings` is download settings only.
> - `AppSettings.CatalogProviderKey` still defaults to `"mediathekview"`, and
>   `Infrastructure/Catalog/MediathekView` survives from the DR-001 era. **Do not delete it yet** —
>   per DR-011 the filmliste is the only full-catalog source available, and it is the fallback if
>   per-show crawling proves too narrow (#49 is on hold).
> - Auth is a single optional API key (see below); no user/identity model.
> - `README.md` is **pre-DR-009 and wholly wrong** (describes the dropped role system, SQLite,
>   `Krautwatch.Worker`, the retired `/api/catalog` surface) — issue #25.

## Architecture (DR-009 — read `docs/architecture/DR-009` before structural changes)

### Hexagonal, four layers

```
Domain  ←  Application  ←  Infrastructure
                 ↑               ↑
             Presentation (hosts + Aspire) — composition root
```

- **Domain** — entities, value objects, enums, **ports** (interfaces). Zero project dependencies.
- **Application** — use-cases as **vertical feature slices** (see below). Depends only on Domain.
- **Infrastructure** — adapters implementing the ports: EF Core + Postgres, broadcaster HTTP
  clients, ffmpeg, the messaging transport. Depends on Application + Domain.
- **Presentation** — every runnable host + the Aspire orchestrator.

### Folder convention — no namespace in folder names

Folders drop the assembly prefix; assemblies/namespaces stay fully qualified:
`src/Domain/Krautwatch.Domain.csproj` → namespace `Krautwatch.Domain`.

### Application = vertical slices, CQRS/A **inside** each slice

Cut the Application layer **by feature, not by operation type**. Each slice is cohesive,
independently testable, and promotable to its own project later. CQRS/A lives *inside* the slice:

```
Application/
├── Catalog/    BrowseCatalog · SearchCatalog · GetEpisodeDetail   (read side for the standalone Web UI)
├── Crawling/   CrawlShow (Action) · CrawlScheduler (BackgroundService, config-driven targets)
├── Downloads/  RunDownload · AddDownloadByToken (Actions) · DownloadHandlers · RefreshProxyList · NzbToken
├── Indexing/   SearchReleases · Release · ReleaseMapper   (Newznab search + RSS read side)
└── Settings/   SettingsHandlers   (download dir, concurrency, refresh interval)
```

| | Touches | Runs on | Example |
|---|---|---|---|
| **Command** | our own state (write) | Api, Agents | `StartDownloadCommand`, `UpsertEpisodes` |
| **Query** | our own state (read) | Api, Web | `SearchReleases` (Newznab), `GetDownloadQueue` (SABnzbd) |
| **Action** | the outside world — **IO-driven** | **Agents** | `CrawlShowHandler`, `RunDownloadHandler` |

**Rule:** Actions orchestrate external IO (via Infrastructure ports) and emit Commands/events;
Commands persist; Queries read.

**File convention (as actually built — no `Action/`/`Command/`/`Query/` subfolders):** slices are
**flat**, one file per use-case named after it (`Crawling/CrawlShow.cs`), and the CQRS/A split is
marked by banner comments *inside* the file:

```csharp
// ============================================================
// Message
// ============================================================
public record CrawlShowCommand(string ProviderKey, string ShowQuery);

// ============================================================
// Action (IO-driven, DR-009)
// ============================================================
public class CrawlShowHandler(...)
```

**Ports live in `Domain/Interfaces/`** — `IBroadcasterCrawler`, `ICatalogProvider`,
`IDownloadProvider`, `IDownloadQueue`, `IEgressProxyProvider`, `IMessageDispatcher`, `IRepositories`.
There is no `Application/Abstractions/`.

### Persistence & messaging

- **Postgres + EF Core** (adapters in `Infrastructure/Persistence`). Aspire provisions Postgres.
  Provider is abstracted (`AddInfrastructure(DbProviderOptions)`) — postgres default, **mssql**
  swappable. (No SQLite single-owner dance — DR-002 is superseded.) **SQLite was removed entirely**
  (unused in production; it dragged in a vulnerable `SQLitePCLRaw` — NU1903). Repository tests run
  against **real Postgres via Testcontainers**, so they need a running Docker daemon.
- **Wolverine** is the mediator + bus + transactional outbox. **The transport is an Infrastructure
  concern**: **Postgres transport by default** (durable, no extra container), **RabbitMQ opt-in** by
  config for scale-out. Application only sees message contracts + a dispatch port.

### Presentation — Aspire single entry, microservice fleet

```
Presentation/
├── AppHost/               .NET Aspire — the single dev entry point (`dotnet run`) that runs the fleet
├── ServiceDefaults/       OTel / health, shared by every host
├── Migrator/              run-to-completion EF-migration owner; consumers WaitForCompletion it
├── Web/                   standalone Blazor-Server UI (search / download / monitor), Application in-process
├── Api/
│   └── NewznabIndexerApi/ Newznab (indexer) + SABnzbd (download client) — the public *arr-facing surface
└── Agents/                (was "Worker")                                   → Actions
    ├── Ard/               ARD (+ KiKA) crawler agent
    ├── Zdf/               ZDF crawler agent
    └── Downloader/        polls the job table → raw MP4 copy, or ffmpeg remux for HLS
```

> The pre-DR-010 browser product (`Api/PublicApi` internal JSON API + the old `Web` browse UI) was
> retired — Sonarr/Radarr drive Krautwatch (DR-010). The current `Web` is a **fresh, purpose-built
> standalone UI** (Blazor Server, talks to Application in-process) for search / manual download /
> monitor without an *arr instance. A genuine *arr **config** UI (Sonarr/Radarr instances, apikey) is
> still future work.

Each host is an **independently deployable microservice** from day one. **Adding a broadcaster** =
a new `Application/<Broadcaster>` slice + an Infrastructure HTTP client + a `Presentation/Agents/<Broadcaster>` host.

### Enforced

This architecture is enforced by **ArchUnitNET** architecture tests in `tests/Architecture.Tests`
(4 rules: Domain depends on no other layer · Application depends only on Domain · Infrastructure
does not depend on Presentation · a slice does not depend on sibling slices). Keep them green.

## Common commands

**[Fallout](https://fallout.build)** (`build/Build.cs`) is the build entry point and what CI runs —
targets `Compile`, `Test`, `TestLive`. It is pinned as a local dotnet tool (`.config/dotnet-tools.json`),
so `dotnet tool restore` first on a fresh clone:

```bash
./build.sh Test          # macOS/Linux — restore + compile + unit tests   (build.cmd on Windows)
./build.sh TestLive      # + Live.Tests: real ARD/ZDF network crawls & downloads (~5 min)
dotnet fallout Test      # same thing via the tool, which is what CI invokes
```

> **`.github/workflows/*.yml` is GENERATED — never hand-edit it.** The workflow is emitted from the
> `[GitHubActions]` attributes on `Build`; editing the YAML directly is silently overwritten on the next
> generation. Change the attribute, then regenerate:
> ```bash
> dotnet fallout --generate-configuration GitHubActions_build --host GitHubActions
> ```

**`Test` needs Docker running** — `Infrastructure.Tests` spins up a Postgres container
(Testcontainers) shared across its repository fixtures via `PostgresCollection`.

Plain SDK commands work too:

```bash
dotnet restore && dotnet build
dotnet test                                    # all tests (incl. Architecture + Live)

# Run the whole fleet locally via Aspire (dashboard on localhost:15000-ish)
dotnet run --project src/Presentation/AppHost

# EF Core migrations — the model + design-time factory live in Infrastructure;
# no startup project needed.
dotnet ef migrations add <Name> --project src/Infrastructure --context AppDbContext
```

`tests/Live.Tests` hit the real broadcasters — they are **not** hermetic and need network (plus
`KRAUTWATCH_TEST_PROXY` for the geo-restricted cases). `dotnet test` runs them; prefer
`./build.sh Test` for a fast inner loop.

### Auth — two separate surfaces (#48)

**Humans (`Presentation/Web`).** Scheme selected by **`Auth:Provider = local | oidc | none`** in the
Web host's composition root, defaulting to **`local`**. The pluggable part is the *scheme*, not a single
Domain interface: local credentials fit a port (`ILocalCredentialStore` + `IPasswordHasher`), while OIDC
is a redirect/token protocol owned by framework middleware with nothing left to abstract. Both land on
the same cookie and `ClaimsPrincipal`, so everything downstream is provider-agnostic.

- First run has no admin, so the Web host logs a **`/setup?token=…`** link. The token is generated per
  process (in memory, rotates on restart) — without it `/setup` is closed, which prevents the
  admin-takeover window you get from leaving setup open until claimed.
- Every routable page **must** carry `[Authorize]` or `[AllowAnonymous]`. Blazor has no fallback policy
  for components, so this is enforced by `PageAuthorizationSpecs` in `tests/Architecture.Tests` rather
  than by memory — a page with no decision fails the build's tests.
- Only `Login`, `Logout` and `Setup` are anonymous, and that list is itself asserted.
- `Auth:Provider = none` (for reverse-proxy forward-auth setups) works via `AnonymousAccess` middleware
  setting `HttpContext.User`, **not** by swapping `AuthenticationStateProvider` — the provider swap only
  takes effect on one of the two render modes.
- Login POSTs are rate-limited (10/min/IP) via a **global** limiter with a no-limiter partition for
  everything else. A named policy on `MapRazorComponents` would throttle the entire UI, since Blazor
  routes every page through that one endpoint.
- The auth pages are deliberately **static SSR** (no `@rendermode`): writing the cookie needs
  `HttpContext` before the response starts, which an interactive circuit cannot do. Interactivity is
  therefore declared per-page, not globally on `<Routes>`.

**Machines (`Api/NewznabIndexerApi`).** One instance API key, config key **`Krautwatch:ApiKey`**, enforced
by `ApiKeyGuard` across both the Newznab and SABnzbd surfaces. Unset = fully open (dev default); `t=caps`
stays open regardless so Prowlarr can probe. This **cannot** become OIDC — Sonarr/Radarr can only send an
`apikey` query parameter.

System dependency: **ffmpeg** on PATH (the Downloader agent's image bundles it).

### Geo-restricted downloads → egress proxy (#45)

Some assets (KiKA / licensed content) are **DACH geo-restricted** — detected at crawl time (ARD
`isGeoBlocked` / ZDF `geoLocation`) and flagged on the `Episode`/`DownloadJob`. The Downloader routes
only those jobs through a German egress proxy; unrestricted downloads go direct. Config (Downloader host):

```
Download:ProxyUrl                     # bring-your-own proxy (recommended: your own DE VPS/WireGuard exit)
Download:ProxyList:Enabled            # opt-in: auto-source free DE proxies from a public list (best-effort)
Download:ProxyList:RefreshInterval    # default 1.00:00:00 (daily) — refreshes the cached `Proxy` table
Download:ProxyList:SourceUrl          # GeoNode DE endpoint by default
Download:ProxyList:Country            # DE
Download:ProxyList:MaxCandidates      # ranked candidates offered per geo-restricted download
```

A geo-restricted job with no egress configured fails fast. Live test: set `KRAUTWATCH_TEST_PROXY` to a
DE proxy and `./build.cmd TestLive` does the real KiKA download (else it just proves the fail-fast).

## Tech stack

- **.NET 10** (DR-007), C# 14. ASP.NET Core Minimal APIs.
- **EF Core 10 + Npgsql** (Postgres).
- **WolverineFx** — messaging/mediator/outbox (Postgres transport default, RabbitMQ opt-in).
- **.NET Aspire** — dev orchestration + docker-compose generation (DR-003).
- **Mapperly** — source-generated entity↔DTO mapping.
- **OpenTelemetry** — logs/metrics/traces; Prometheus `/metrics`, Grafana in prod (DR-006).
- **FluentValidation**; tests on **xUnit + Shouldly + NSubstitute + ArchUnitNET**.

## Architecture decisions

`docs/architecture/` — **DR-009 (architecture reset), DR-010 (arr-indexer direction) and DR-011
(search-driven indexing) are current.** DR-011 retracts DR-010's reach-back clause: the Sonarr monitored
list is *not* the crawl work-list.
DR-002/004/008 are superseded; DR-001/003/005/006/007 still apply (some refined). Read the current
DRs before structural changes.

## Plans

**Always persist a plan before executing it.** When a plan is agreed, write it to `docs/plans/` as
`YYYY-MM-DD - <title>.md` before implementation begins.
