# Krautwatch

Self-hosted **Newznab indexer + SABnzbd-compatible download client** for German public TV
(ARD, ZDF, KiKA, …) — in the spirit of MediathekArr. **Sonarr/Radarr drive it:** they search the
indexer, send grabs to the download client, and Krautwatch pulls the actual stream to disk.

Built on .NET 10, Postgres, Wolverine and .NET Aspire.

> **Status: v0.1.1 — working end-to-end, but early.** The full round-trip is live: a Newznab search
> resolves against the broadcaster on demand (so an uncrawled show still returns results), Sonarr grabs,
> our SABnzbd surface accepts the NZB, and the Downloader agent fetches the progressive-MP4 or HLS stream
> (through a German egress proxy for DACH-geo-restricted assets). There is a Blazor UI for search, manual
> downloads, `*arr` instances and show mappings, behind a login.
>
> Install it from the [latest release](../../releases/latest) — `docker-compose.yaml` and `env.example`
> ship as release assets, and multi-arch images (amd64 + arm64) are on `ghcr.io/chrison-dev/`.
>
> **What is *not* there yet:** **Sonarr's own import step has never been proven** — everything up to it
> has, against a real Sonarr 4.0.19, but the final hop needs Krautwatch and Sonarr to share a filesystem
> (see [`KRAUTWATCH_DOWNLOADS`](#the-one-setting-that-matters-krautwatch_downloads)); **OIDC**
> ([#48](../../issues/48) — local login works, `oidc` is a stub); **subtitles** (the URL is parsed at
> crawl time but never persisted or fetched — [#20](../../issues/20)); **`*arr` reach-back** to pre-warm
> the crawl list ([#6](../../issues/6) — optional by design, see DR-011); and a **real download queue**
> (priority + honouring `MaxConcurrentDownloads` — [#51](../../issues/51)). `*arr` API keys are stored
> **in plaintext** ([#60](../../issues/60)).

---

## Prerequisites

- **.NET 10 SDK** (`10.0.100+`, see `global.json`)
- **Docker** — Aspire provisions the Postgres container, and the repository tests use Testcontainers
- **ffmpeg** on PATH — `brew install ffmpeg` (used to remux HLS streams; the Downloader image bundles it)

> No Aspire *workload* install is needed. Aspire 13 comes in via the `Aspire.AppHost.Sdk` reference in
> `Krautwatch.AppHost.csproj` plus NuGet packages — the old `dotnet workload install aspire` step is
> legacy and does not apply.

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

## Deploying with Docker Compose

The compose file is **generated from the same Aspire model the dev fleet runs**, so the deployed
topology cannot drift from the one that is tested daily.

**To just run it,** take `docker-compose.yaml` + `env.example` from the
[latest release](../../releases/latest), rename the latter to `.env`, and fill it in — the images it
references are already published, so there is nothing to build.

**To build your own** (a dev build, or a change you have not released):

```bash
./build.sh Images --image-tag 0.1.0      # six service images
./build.sh Compose                       # .artifacts/compose/{docker-compose.yaml,.env}
```

Fill in `.artifacts/compose/.env` — it is generated with every key present and empty:

```dotenv
MIGRATOR_IMAGE=ghcr.io/chrison-dev/krautwatch-migrator:0.1.0
NEWZNAB_IMAGE=ghcr.io/chrison-dev/krautwatch-newznab:0.1.0
WEB_IMAGE=ghcr.io/chrison-dev/krautwatch-web:0.1.0
AGENT_ARD_IMAGE=ghcr.io/chrison-dev/krautwatch-agent-ard:0.1.0
AGENT_ZDF_IMAGE=ghcr.io/chrison-dev/krautwatch-agent-zdf:0.1.0
AGENT_DOWNLOADER_IMAGE=ghcr.io/chrison-dev/krautwatch-agent-downloader:0.1.0

POSTGRES_PASSWORD=<generate one>
KRAUTWATCH_APIKEY=<generate one>        # required — see below
TVDB_APIKEY=<optional>

KRAUTWATCH_DOWNLOADS=/mnt/media/downloads   # host path, see below
```

```bash
cd .artifacts/compose && docker compose up -d --wait
```

`newznab` is published on **:5055**, the web UI on **:5099**, and the Aspire dashboard on **:18888**.
Postgres keeps its data in the named volume `krautwatch-pgdata`, so `docker compose down` does not
discard your catalog — `down -v` does.

### The one setting that matters: `KRAUTWATCH_DOWNLOADS`

Sonarr imports by reading the file the Downloader wrote, so **both containers must see it at the same
path**. Mount the same host directory into Sonarr at `/downloads` too:

```yaml
# in your existing *arr stack
services:
  sonarr:
    volumes:
      - /mnt/media/downloads:/downloads     # identical host path and container path
```

Matching both sides is what avoids Sonarr's remote-path mapping, which is the most common way an
otherwise-working setup ends with "No files found are eligible for import".

### `KRAUTWATCH_APIKEY` is required, not optional

Sonarr refuses to configure a SABnzbd download client without an API key, so an empty value is not a
working deployment. `t=caps` stays open regardless, so Prowlarr can still probe the indexer.

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

> ⚠️ The indexer/download-client surface is protected only by `Krautwatch:ApiKey`, and **unset means wide
> open**. The `web` UI is authenticated (see below), but this machine-facing surface is not — `*arr` apps
> can only send an `apikey`, so it cannot use the UI's login. Don't expose it to the internet yet.

---

## Configuration

### What gets crawled

This is the **standing crawl list**, which feeds the RSS feed — it is *not* what search is limited to.
Searches resolve on demand ([see below](#what-gets-searched-query-driven-dr-011)), so a show absent from
this list is still findable; the list exists because RSS-Sync polls with no particular target. Pulling it
from your `*arr` monitored series is an optional future pre-warm ([#6](../../issues/6)), not a
requirement. Each agent binds a `Crawl` section, falling back to seed shows (`Extra 3`, `Biene Maja` on
ARD/KiKA; `heute-show` on ZDF):

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

### Authentication

The UI requires a sign-in. `Auth:Provider` selects how:

| Value | Behaviour |
|---|---|
| `local` (default) | Built-in single administrator, created on first run |
| `oidc` | Delegate to your own identity provider — Authentik, Keycloak, Authelia, Entra *(not yet implemented)* |
| `none` | No authentication — only for deployments already behind reverse-proxy forward-auth |

**First run:** there is no administrator yet, so the `web` host logs a one-time setup link. Fetch it from
the logs and open it:

```bash
docker compose logs web    # or the Aspire dashboard's log view
#  warn: Krautwatch has no administrator yet.
#        Open /setup?token=4gvr4kVGq_cVlcuT_siuL3SGhEQ to create one.
```

The token is required — `/setup` is closed without it, so nobody on your network can claim the instance
before you do. It lives in memory only and rotates if the process restarts. Once an administrator exists,
`/setup` never reopens.

### What gets searched (query-driven, DR-011)

A Newznab search for a show nothing has crawled yet **resolves it live** against the broadcasters, so
Krautwatch works with no `*arr` configuration at all.

**How the first search behaves is your choice** (a setting, editable in the UI — not a rebuild):

| Mode | Behaviour |
|---|---|
| **Return results fast** *(default)* | Answer after a short wait with whatever has resolved so far, and let the crawl finish in the background. The first search may under-report; the next one is complete and instant. Advanced: set the wait in seconds (1–300, default 8). |
| **Wait for complete result on first query** | Wait for the resolution to finish so the first search is already complete. Slower — and if it exceeds Sonarr's own indexer timeout, Sonarr may treat the indexer as failing. Still bounded by `CrawlTimeout`; no wait is ever unbounded. |

Operational knobs stay in config:

```
Indexing:OnDemandResolution:Enabled                   # default true — kill switch
Indexing:OnDemandResolution:CrawlTimeout              # default 00:02:00 — background crawl budget,
                                                      #   and the ceiling on "wait for complete"
Indexing:OnDemandResolution:PositiveTtl               # default 06:00:00 — trust a hit this long
Indexing:OnDemandResolution:NegativeTtl               # default 00:45:00 — trust a miss this long
Indexing:OnDemandResolution:MaxConcurrentResolutions  # default 2 — politeness cap toward ARD/ZDF
```

The RSS feed (no query) is never resolved — it serves the standing crawl list, since RSS-Sync polls
constantly with no particular target.

### TheTVDB matching (optional but strongly recommended)

Sonarr identifies a series by its **TVDB id**, and its episode search always sends `season=` and `ep=`.
German public-TV titles rarely survive that: Sonarr stores *Die Biene Maja* as **"Maya the Bee"**, our ARD
feed calls *Extra 3* `extra 3 · Der Irrsinn der Woche`, and most Mediathek assets carry an air date but no
episode number at all. Krautwatch closes the gap by resolving the id Sonarr sends against TheTVDB and
matching it back onto the catalog — which also yields the season/episode numbers needed to emit
`Show.S2026E17.GERMAN.1080p.WEB.h264` instead of an unmatchable date.

Get a free key from [TheTVDB's API key dashboard](https://www.thetvdb.com/dashboard/account/apikey)
(a subscriber PIN is optional). Supply it either way:

```bash
# Development — stays out of the repo
dotnet user-secrets set "TvdbConfiguration:ApiKey" <key> --project src/Presentation/Api/NewznabIndexerApi
dotnet user-secrets set "TvdbConfiguration:ApiKey" <key> --project src/Presentation/Web

# Production — environment variables (note the double underscore)
TvdbConfiguration__ApiKey=<key>
TvdbConfiguration__Pin=<pin>        # optional, subscribers only
```

Or paste it into **Settings → TheTVDB** in the UI, which stores it in the database instead.

**Configuration wins over the stored value.** An operator who sets `TvdbConfiguration__ApiKey` in a compose
file expects it to apply, so when it is present the settings page shows the key as managed by configuration
and read-only — being silently overridden by a stale row from an earlier UI edit is a bad afternoon.

**Without a key nothing breaks, it just matches worse:** every TVDB call returns nothing, releases are
emitted without a `tvdbid` attribute, and Sonarr falls back to parsing our titles. A TVDB outage behaves the
same way, deliberately — Sonarr disables an indexer that keeps erroring, so a third-party outage must never
cost you the indexer.

> Entered through the UI the key is stored **as you typed it**, in plain text, like the `*arr` instance
> keys. To keep it out of the database entirely, store a **secret reference** instead — see below.

### Keeping secrets out of the database

Anything you can enter as a credential in the UI — an `*arr` instance API key, the TheTVDB key — can be
stored as a **pointer to the secret** rather than the secret itself:

| Stored value | Meaning |
|---|---|
| `abc123def456` | the key itself — plain text in Postgres, the default when you paste one in |
| `env:SONARR_API_KEY` | read from that environment variable |
| `file:/run/secrets/sonarr` | read from that file — a Docker/Kubernetes mounted secret |
| `literal:env:weird-key` | take the rest literally, for the rare key that starts with a scheme |

So a compose deployment can keep every credential in `.env` or a secrets mount, and a database dump
contains **no credentials at all** — which is stronger than encrypting them, and there is no key ring to
back up or lose. Paste the reference into the settings field exactly as you would a key.

```yaml
services:
  web:
    environment:
      SONARR_API_KEY: ${SONARR_API_KEY}      # then store "env:SONARR_API_KEY" in the UI
    # or, with a secrets mount:
    secrets: [sonarr_key]                    # then store "file:/run/secrets/sonarr_key"
```

Two things to know:

- **A reference is resolved by the container that uses it.** Set the variable, or mount the file, in
  every host that needs it. If it is missing, the settings page says so on the row and the connection
  test names the variable — rather than authenticating with an empty key and reporting a confusing 401.
- **References are not hidden in the UI**, because a pointer is not a credential — you need to see which
  variable is wired. Literal keys stay masked.

> **What this does and does not protect.** A reference keeps the secret out of database dumps, backups,
> snapshots and stolen volumes — the realistic leak path for a self-hosted app. It does **not** protect a
> compromised application host: the app has to read the secret, so anything running as the app can too.
> Encryption at rest has exactly the same limitation. See
> [`docs/plans/2026-08-09 - secret-handling.md`](docs/plans/2026-08-09%20-%20secret-handling.md).

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

Decision records live in [`docs/architecture/`](docs/architecture/). The current ones are
**[DR-009](docs/architecture/DR-009-architecture-reset.md)** (architecture reset),
**[DR-010](docs/architecture/DR-010-arr-indexer-direction.md)** (the `*arr` indexer direction) and
**[DR-011](docs/architecture/DR-011-search-driven-indexing.md)** (search-driven indexing, which
retracts DR-010's clause about the Sonarr monitored list being the crawl work-list).
[`CLAUDE.md`](CLAUDE.md) is the working guide to the layout and conventions.

---

## Build & test

[Fallout](https://fallout.build) (`build/Build.cs`) is the build entry point and what CI runs. It is
pinned as a local dotnet tool, so run `dotnet tool restore` once on a fresh clone:

```bash
./build.sh Test        # restore + compile + unit/architecture tests   (build.cmd on Windows)
./build.sh TestLive    # + Live.Tests — real ARD/ZDF crawls and downloads (~5 min, needs network)
```

The GitHub Actions workflows under `.github/workflows/` are **generated** from the `[GitHubActions]`
attributes on the build class — edit `build/Build.cs`, not the YAML, or your change is overwritten:

```bash
dotnet fallout --generate-configuration GitHubActions_build --host GitHubActions
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
