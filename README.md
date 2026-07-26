# Krautwatch

Self-hosted **Newznab indexer + SABnzbd-compatible download client** for German public TV
(ARD, ZDF, KiKA, …) — in the spirit of MediathekArr. **Sonarr/Radarr drive it:** they search the
indexer, send grabs to the download client, and Krautwatch pulls the actual stream to disk.

Built on .NET 10, Postgres, Wolverine and .NET Aspire.

> **Status: working end-to-end, but early.** The full round-trip is live — per-broadcaster crawler
> agents build the catalog, the Newznab surface serves it, and the Downloader agent fetches
> progressive-MP4 or HLS streams (with a German egress proxy for DACH-geo-restricted assets). What is
> *not* there yet: **Sonarr/Radarr reach-back** (the crawl work-list is still config/hardcoded, not
> pulled from your `*arr` monitored series — [#6](../../issues/6)), an ***arr instance config UI***
> ([#4](../../issues/4)), **real authentication** ([#48](../../issues/48)), **subtitles** (the URL is
> parsed at crawl time but never persisted or fetched — [#20](../../issues/20)) and a **production
> compose file** ([#24](../../issues/24)).

---

## Prerequisites

- **.NET 10 SDK** (`10.0.100+`, see `global.json`)
- **.NET Aspire workload** — `dotnet workload install aspire`
- **Docker** — Aspire provisions the Postgres container
- **ffmpeg** on PATH — `brew install ffmpeg` (used to remux HLS streams; the Downloader image bundles it)

---

## Running the fleet locally

```bash
dotnet restore
dotnet run --project src/Presentation/AppHost
```

Aspire starts everything and prints its dashboard URL (~`localhost:15000`). The fleet:

| Resource | What it is |
|---|---|
| `postgres` / `krautwatch` | database, provisioned by Aspire |
| `migrator` | run-to-completion EF-migration owner — every consumer waits for it |
| `newznab` | the public `*arr`-facing surface: Newznab indexer + SABnzbd download client |
| `web` | standalone Blazor Server UI — search / download / monitor **without** an `*arr` |
| `agent-ard` | ARD (+ KiKA) crawler |
| `agent-zdf` | ZDF crawler |
| `agent-downloader` | claims queued jobs and pulls streams to disk |

Launch the `Observability` profile to also get Prometheus, Grafana and Loki containers.

---

## Wiring up Sonarr / Radarr / Prowlarr

Point both at the `newznab` host (take its URL from the Aspire dashboard).

**As an indexer** — in Prowlarr/Sonarr add a *Newznab* indexer:

| Field | Value |
|---|---|
| URL | `http://<host>:<port>` |
| API Path | `/api` |
| API Key | whatever you set as `Krautwatch:ApiKey` (leave blank if unset) |

```
GET /api?t=caps                              # capabilities (always open, so Prowlarr can probe)
GET /api?t=tvsearch&q=heute-show&apikey=…    # RSS 2.0 + newznab: attrs
GET /api?t=search&q=…&apikey=…
GET /download?…                              # opaque per-episode token → the grab
```

**As a download client** — add a *SABnzbd* client pointing at the same host. Supported modes:
`version`, `get_config`, `addurl`, `addfile`, `queue`, `history`.

Release titles follow Sonarr's model: shows detected as `Standard` get `… S02E52 …`, everything else
stays `Daily` and gets `… 2026-07-10 …`. Most German public-TV content is daily/dated.

> ⚠️ Auth today is one optional API key (`Krautwatch:ApiKey`). **Unset means wide open**, and the
> `web` UI has no auth at all — don't expose either to the internet yet. See
> [#48](../../issues/48).

---

## Configuration

### What gets crawled

The work-list is **not yet pulled from Sonarr** ([#6](../../issues/6)). Each agent binds a `Crawl`
section, falling back to seed shows (`Extra 3`, `Biene Maja` on ARD/KiKA; `heute-show` on ZDF):

```jsonc
{
  "Crawl": {
    "Interval": "06:00:00",
    "InitialDelay": "00:00:10",
    "Targets": [
      { "ProviderKey": "ard",  "ShowQuery": "Extra 3" },
      { "ProviderKey": "kika", "ShowQuery": "Biene Maja" },
      { "ProviderKey": "zdf",  "ShowQuery": "heute-show" }
    ]
  }
}
```

### Downloads

`Download:Directory` sets the output path (the dev fleet points it at a temp dir; in production it's
your volume mount).

### Geo-restricted content → German egress proxy

Some assets (KiKA, licensed cartoons) are **DACH-geo-restricted** — detected at crawl time from the
broadcasters' own flags (ARD `isGeoBlocked`, ZDF `geoLocation`) and carried on the `Episode` /
`DownloadJob`. Only those jobs route through a proxy; everything else goes direct. A geo-restricted
job with no egress configured **fails fast with a clear message**.

```
Download:ProxyUrl                     # bring-your-own (recommended: your own DE VPS / WireGuard exit)
Download:ProxyList:Enabled            # opt-in: auto-source free DE proxies from a public list (best-effort)
Download:ProxyList:RefreshInterval    # default 1.00:00:00
Download:ProxyList:SourceUrl          # GeoNode DE endpoint by default
Download:ProxyList:Country            # DE
Download:ProxyList:MaxCandidates      # ranked candidates tried per download
```

### Database

Postgres by default; the provider is abstracted (`AddInfrastructure(DbProviderOptions)`) with
`mssql` swappable by config.

---

## Architecture

Hexagonal, four layers, enforced by ArchUnitNET tests:

```
Domain  ←  Application  ←  Infrastructure
                 ↑               ↑
             Presentation (hosts + Aspire) — composition root
```

- **Domain** — entities, enums, and the **ports** (`Domain/Interfaces`). Zero project dependencies.
- **Application** — use-cases as **vertical feature slices** (`Catalog`, `Crawling`, `Downloads`,
  `Indexing`, `Settings`), CQRS/A *inside* each slice.
- **Infrastructure** — the adapters: EF Core + Npgsql, ARD/ZDF HTTP clients, ffmpeg, proxies, Wolverine transport.
- **Presentation** — every runnable host plus the Aspire orchestrator.

Wolverine is the mediator + bus + transactional outbox (**Postgres transport** by default — durable,
no extra container; RabbitMQ opt-in for scale-out).

Each host is an independently deployable microservice. **Adding a broadcaster** = a new Application
slice + an Infrastructure HTTP client + a `Presentation/Agents/<Broadcaster>` host.

Decision records live in [`docs/architecture/`](docs/architecture/) — **[DR-009](docs/architecture/DR-009-architecture-reset.md)**
(architecture reset) and **[DR-010](docs/architecture/DR-010-arr-indexer-direction.md)** (the `*arr`
indexer direction) are the current ones. [`CLAUDE.md`](CLAUDE.md) is the working guide to the layout
and conventions.

---

## Build & test

Nuke (`build/Build.cs`) is the build entry point and what CI runs:

```bash
./build.sh Test        # restore + compile + unit/architecture tests   (build.cmd on Windows)
./build.sh TestLive    # + Live.Tests — real ARD/ZDF crawls and downloads (~5 min, needs network)
```

`Test` needs **Docker running**: the repository tests execute against a real Postgres container
(Testcontainers) rather than an in-memory stand-in, so provider behaviour matches production.

Plain SDK commands work too (`dotnet build`, `dotnet test` — note `dotnet test` includes the live
tests). Set `KRAUTWATCH_TEST_PROXY` to a DE proxy to exercise the geo-restricted download path for
real; without it that test just proves the fail-fast.

### EF Core migrations

The model and design-time factory both live in Infrastructure — no startup project needed:

```bash
dotnet ef migrations add <Name> --project src/Infrastructure --context AppDbContext
```

`Presentation/Migrator` applies them at fleet startup.

---

## Legal

Krautwatch downloads freely available content from German public broadcasters' own official APIs for
personal, offline use — the same thing their own websites and apps do. It circumvents no DRM. Respect
your local law and the broadcasters' terms; geo-restriction routing is intended for licence-fee
payers accessing content they are already entitled to.
