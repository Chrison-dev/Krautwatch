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
> `CoreWorker`/`Worker` role hosts dropped). Postgres + durable Wolverine (Postgres transport) are wired and the API runs EF migrations at
> startup. The **ARD (+KiKA) and ZDF agents crawl their configured shows into Postgres** via the
> `Application/Crawling` Action (broadcaster clients behind the `IBroadcasterCrawler` port; the
> scheduler dispatches `CrawlShowCommand` through the `IMessageDispatcher` port over the durable bus).
> Still to come: the **Newznab/SABnzbd** API surface and the Downloader agent's real ffmpeg pull.
> The superseded `docker/` topology (DR-004) is dead and will be replaced by Aspire-generated
> compose in the distribution milestone.

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
├── Crawling/   Action/ (ArdCrawling.cs, ZdfCrawling.cs) · Command/ · Query/
├── Downloads/  Action/ · Command/ · Query/
├── Indexing/   Query/ …   (Newznab search + RSS read side)
├── Settings/   Command/ · Query/   (Sonarr/Radarr instances)
└── Abstractions/  (ports the adapters implement)
```

| | Touches | Runs on | Example |
|---|---|---|---|
| **Command** | our own state (write) | Api, Agents | `EnqueueDownload`, `UpsertEpisodes` |
| **Query** | our own state (read) | Api | `SearchCatalog` (Newznab), `GetQueue` (SABnzbd) |
| **Action** | the outside world — **IO-driven** | **Agents** | `ArdCrawling`, `ResolveStream`, `DownloadEpisode` |

**Rule:** Actions orchestrate external IO (via Infrastructure ports) and emit Commands/events;
Commands persist; Queries read. A new file goes in `Application/<Slice>/<Action|Command|Query>/`.

### Persistence & messaging

- **Postgres + EF Core** (adapters in `Infrastructure/Persistence`). Aspire provisions Postgres.
  Provider is abstracted (`AddInfrastructure(DbProviderOptions)`) — postgres default, sqlite/mssql
  swappable. (No SQLite single-owner dance — DR-002 is superseded.)
- **Wolverine** is the mediator + bus + transactional outbox. **The transport is an Infrastructure
  concern**: **Postgres transport by default** (durable, no extra container), **RabbitMQ opt-in** by
  config for scale-out. Application only sees message contracts + a dispatch port.

### Presentation — Aspire single entry, microservice fleet

```
Presentation/
├── AppHost/          .NET Aspire — the single dev entry point (`dotnet run`) that runs the fleet
├── ServiceDefaults/  OTel / health, shared by every host
├── Api/              Newznab + SABnzbd + RSS  (the *arr-facing surface)   → Queries + Commands
├── Web/              Blazor instance-config UI
└── Agents/           (was "Worker")                                        → Actions
    ├── Ard/          ARD (+ KiKA) crawler agent
    ├── Zdf/          ZDF crawler agent
    └── Downloader/   ffmpeg download execution
```

Each host is an **independently deployable microservice** from day one. **Adding a broadcaster** =
a new `Application/<Broadcaster>` slice + an Infrastructure HTTP client + a `Presentation/Agents/<Broadcaster>` host.

### Enforced

This architecture is enforced by **ArchUnitNET** architecture tests in `tests/Architecture.Tests`
(layer-dependency rules, slice isolation). Keep them green.

## Common commands

```bash
dotnet restore && dotnet build
dotnet test                                    # all tests (incl. Architecture)

# Run the whole fleet locally via Aspire (dashboard on localhost:15000-ish)
dotnet run --project src/Presentation/AppHost

# EF Core migrations — model lives in Infrastructure, a host is the startup project
dotnet ef migrations add <Name> \
  --project src/Infrastructure --startup-project src/Presentation/Api
```

System dependency: **ffmpeg** on PATH (the Downloader agent's image bundles it).

## Tech stack

- **.NET 10** (DR-007), C# 14. ASP.NET Core Minimal APIs, Blazor.
- **EF Core 10 + Npgsql** (Postgres).
- **WolverineFx** — messaging/mediator/outbox (Postgres transport default, RabbitMQ opt-in).
- **.NET Aspire** — dev orchestration + docker-compose generation (DR-003).
- **OpenTelemetry** — logs/metrics/traces; Prometheus `/metrics`, Grafana in prod (DR-006).
- **FluentValidation**; tests on **xUnit + Shouldly + NSubstitute + ArchUnitNET**.

## Architecture decisions

`docs/architecture/` — **DR-009 (architecture reset) and DR-010 (arr-indexer direction) are current.**
DR-002/004/008 are superseded; DR-001/003/005/006/007 still apply (some refined). Read the current
DRs before structural changes.

## Plans

**Always persist a plan before executing it.** When a plan is agreed, write it to `docs/plans/` as
`YYYY-MM-DD - <title>.md` before implementation begins.
