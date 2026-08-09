# Self-hosting Krautwatch

Step by step, for a NAS, a home server or a Pi. Assumes you already run Sonarr or Radarr and know your
way around `docker compose`.

> **Read this first — two known setup blockers.** The full round trip *is* proven, including Sonarr's
> import, against released images and a real Sonarr 4.0.19. But two open bugs will stop you following
> this guide verbatim:
>
> - **[#96](https://github.com/Chrison-dev/Krautwatch/issues/96)** — set the SABnzbd client's **URL Base
>   to `/sabnzbd`**, or it cannot be added at all. Noted again in §5.
> - **[#95](https://github.com/Chrison-dev/Krautwatch/issues/95)** — Sonarr cannot search a **daily**
>   series, which is most German public TV. Shows with real `SxxExx` numbering work. Until this is fixed,
>   dated shows have to be downloaded from Krautwatch's own UI.

---

## 1. What you need

- **Docker** with `docker compose` v2.
- **A Sonarr or Radarr instance**, or nothing at all — the web UI can search and download on its own.
- **~1 GB RAM free** for the stack (seven containers, most of them idle), plus disk for what you download.
- **A 64-bit OS on ARM** if you are on a Pi. Images are published for `linux/amd64` and `linux/arm64`;
  32-bit Raspberry Pi OS will not run them.

No .NET, no ffmpeg, no Postgres on the host — all containerised. ffmpeg ships inside the downloader image.

---

## 2. Install

Take the two files from the [latest release](https://github.com/Chrison-dev/Krautwatch/releases/latest):

```bash
mkdir -p ~/krautwatch && cd ~/krautwatch
curl -LO https://github.com/Chrison-dev/Krautwatch/releases/latest/download/docker-compose.yaml
curl -Lo .env https://github.com/Chrison-dev/Krautwatch/releases/latest/download/env.example
```

The compose file is **generated from the same model the developers run**, so the topology you deploy is
the one that gets tested. Now fill in `.env`:

```dotenv
POSTGRES_PASSWORD=<a long random string>
KRAUTWATCH_APIKEY=<a long random string>
TVDB_APIKEY=                      # optional but strongly recommended — see §6

# NOT in env.example, but you almost certainly want it — see §3
KRAUTWATCH_DOWNLOADS=/mnt/media/downloads
```

Generate the two secrets with something like `openssl rand -hex 24`.

```bash
docker compose up -d
```

| Service | Where | What it is |
|---|---|---|
| `web` | `:5099` | the UI — search, downloads, settings |
| `newznab` | `:5055` | what Sonarr/Radarr/Prowlarr talk to |
| `compose-dashboard` | `:18888` | Aspire telemetry dashboard — logs and traces |
| `postgres` | internal | the catalog, in the `krautwatch-pgdata` volume |
| `migrator` | runs once | applies the schema, then exits — the others wait for it |
| `agent-ard` / `agent-zdf` | internal | the crawlers |
| `agent-downloader` | internal | pulls streams to disk |

`migrator` exiting is **success**, not a crash. Everything else waits on it via
`service_completed_successfully`.

> The dashboard on `:18888` prints its own login token in `docker compose logs compose-dashboard`. It is
> a debugging tool — do not expose it to the internet, and delete the `compose-dashboard` service if you
> do not want it.

---

## 3. The setting that decides whether this works: `KRAUTWATCH_DOWNLOADS`

Sonarr imports by **reading the file the downloader wrote**. So both containers have to see that file, at
the same path.

> ⚠️ **Give this a real path.** Leaving it blank has exactly the same effect as omitting it — the
> downloader writes into a `./downloads` folder next to your compose file and Sonarr never finds
> anything. (On v0.1.1 and earlier the key is missing from `env.example` altogether,
> [#83](https://github.com/Chrison-dev/Krautwatch/issues/83) — add the line yourself.)

Set it to a host path, and mount that **same host path into Sonarr at the same container path**:

```dotenv
# ~/krautwatch/.env
KRAUTWATCH_DOWNLOADS=/mnt/media/downloads
```

```yaml
# your existing *arr stack
services:
  sonarr:
    volumes:
      - /mnt/media/downloads:/downloads     # identical on both sides
```

Krautwatch's downloader always writes to `/downloads` inside its container, so matching Sonarr's container
path to `/downloads` too means **no remote-path mapping** — which is the most common way an otherwise
working setup ends at *"No files found are eligible for import"*.

Both containers also need to **agree about ownership**. If Sonarr runs as `PUID=1000` and the downloader
writes as root, Sonarr may see the file and be unable to move it.

---

## 4. First run: create your administrator

The UI requires a sign-in, and on first boot there is no account. The `web` container logs a one-time
setup link:

```bash
docker compose logs web | grep setup
#  warn: Krautwatch has no administrator yet.
#        Open /setup?token=4gvr4kVGq_cVlcuT_siuL3SGhEQ to create one.
```

Open `http://<host>:5099/setup?token=…` and create the account.

The token is **required** — without it `/setup` is closed, so nobody else on your network can claim the
instance before you do. It lives in memory and rotates if the container restarts (just re-read the logs).
Once an administrator exists, `/setup` never reopens.

`Auth:Provider` selects the scheme:

| `Auth__Provider` | Behaviour |
|---|---|
| `local` *(default)* | built-in single administrator, as above |
| `none` | no authentication — **only** behind reverse-proxy forward-auth |
| `oidc` | **not implemented yet** ([#48](https://github.com/Chrison-dev/Krautwatch/issues/48)) |

---

## 5. Wire up Sonarr / Radarr / Prowlarr

Point both at the **`newznab`** service on port `5055` — not the web UI.

**As an indexer** (Prowlarr, or Sonarr → Settings → Indexers → *Newznab*):

| Field | Value |
|---|---|
| URL | `http://<host>:5055` |
| API Path | `/api` |
| API Key | your `KRAUTWATCH_APIKEY` |

**As a download client** (Settings → Download Clients → *SABnzbd*):

| Field | Value |
|---|---|
| Host / Port | `<host>` / `5055` |
| **URL Base** | **`/sabnzbd`** — required ([#96](https://github.com/Chrison-dev/Krautwatch/issues/96)); leaving it blank fails the connection test |
| API Key | your `KRAUTWATCH_APIKEY` |
| Category | `tv` |

> `KRAUTWATCH_APIKEY` is **not optional in practice**: Sonarr refuses to save a SABnzbd client without an
> API key. Leaving it blank leaves the machine surface wide open, and `t=caps` stays open regardless so
> Prowlarr can still probe.

Verify the indexer by hand before trusting it:

```bash
curl "http://<host>:5055/api?t=caps"
curl "http://<host>:5055/api?t=tvsearch&q=heute-show&apikey=$KRAUTWATCH_APIKEY"
```

**Release naming.** Shows TheTVDB identifies as `Standard` get `Show.S2026E17.GERMAN.1080p.WEB.h264`;
everything else stays `Daily` and gets a date. Most German public-TV content is daily/dated. If Sonarr
grabs nothing despite the search returning results, the series is usually configured with a type that
cannot match dated releases.

---

## 6. TheTVDB (optional, strongly recommended)

Sonarr identifies a series by **TVDB id** and always sends `season=` and `ep=`. German public-TV titles
rarely survive that: Sonarr stores *Die Biene Maja* as *"Maya the Bee"*, ARD calls *Extra 3*
`extra 3 · Der Irrsinn der Woche`, and most Mediathek assets carry an air date but no episode number.

Get a free key from [TheTVDB](https://www.thetvdb.com/dashboard/account/apikey), put it in `.env` as
`TVDB_APIKEY`, and restart. Or paste it into **Settings → TheTVDB** in the UI.

Configuration wins over the UI value: if `TVDB_APIKEY` is set, the settings page shows the key as managed
by configuration and read-only, so a stale row from an earlier UI edit cannot silently override your
compose file.

**Without a key nothing breaks** — every TVDB call returns nothing, releases carry no `tvdbid`, and Sonarr
falls back to parsing titles. A TVDB outage behaves identically and deliberately: Sonarr disables an
indexer that keeps erroring, so a third-party outage must never cost you the indexer.

---

## 7. Keeping credentials out of the database

Anything you can type as a credential — an `*arr` instance API key, the TheTVDB key — can instead be a
**pointer** to the secret:

| What you store | Meaning |
|---|---|
| `abc123def456` | the key itself, in plain text in Postgres |
| `env:SONARR_API_KEY` | read from that environment variable |
| `file:/run/secrets/sonarr` | read from that mounted file |
| `literal:env:weird-key` | take it literally, for a key that starts with a scheme |

```yaml
services:
  web:
    environment:
      SONARR_API_KEY: ${SONARR_API_KEY}     # then store "env:SONARR_API_KEY" in the UI
```

**A reference is resolved by the container that uses it**, so set the variable or mount the file in every
host that needs it. If it is missing, the settings row says so and the connection test names the variable
— rather than authenticating with an empty key and reporting a 401 you cannot explain.

References are shown **unmasked** in the UI on purpose: a pointer is not a credential, and you need to see
which variable is wired. Literal keys stay masked.

> **What this protects.** A reference keeps the secret out of database dumps, backups, snapshots and
> stolen volumes — the realistic leak path for a self-hosted app. It does **not** protect a compromised
> host: the app must read the secret, so anything running as the app can too.

---

## 8. Geo-restricted content

Some assets — KiKA, licensed cartoons — are **DACH geo-restricted**, detected at crawl time from the
broadcasters' own flags. Only those downloads route through a proxy; everything else goes direct, and a
geo-restricted job with no egress configured **fails fast with a clear message** rather than hanging.

Add to the `agent-downloader` service:

```yaml
    environment:
      Download__ProxyUrl: "http://10.0.0.9:3128"    # your own DE VPS / WireGuard exit
```

There is also an opt-in mode that sources free DE proxies from a public list. It is best-effort and
untrusted — reach for it only if you have no exit of your own:

```yaml
      Download__ProxyList__Enabled: "true"
      Download__ProxyList__MaxCandidates: "5"
      Download__ProxyList__RefreshInterval: "1.00:00:00"
```

If you are outside Germany and everything geo-restricted fails, this section is why.

---

## 9. What gets crawled, and what gets searched

These are different, and the difference confuses people.

**Search resolves on demand.** A Newznab search for a show nothing has crawled goes and looks it up live,
so Krautwatch is useful with zero configuration. How the first search behaves is a setting in
**Settings → Search**: answer fast with whatever resolved so far *(default)*, or wait for a complete
result. Waiting is slower, and if it exceeds Sonarr's own indexer timeout Sonarr may mark the indexer as
failing — which is why fast is the default.

**The RSS feed serves a standing crawl list.** RSS-Sync polls constantly with no particular target, so it
cannot resolve on demand. That list is config-driven per agent, and defaults to a few seed shows:

```yaml
  agent-zdf:
    environment:
      Crawl__Interval: "06:00:00"
      Crawl__Targets__0__ProviderKey: "zdf"
      Crawl__Targets__0__ShowQuery: "heute-show"
```

`ProviderKey` is `ard`, `kika` (both on `agent-ard`) or `zdf`. Pre-warming this list from your `*arr`
monitored series is [#6](https://github.com/Chrison-dev/Krautwatch/issues/6) and optional by design.

---

## 10. Platform notes

**Unraid.** No Community Applications template yet. Use *Docker → Compose Manager*, paste the compose
file, and set `KRAUTWATCH_DOWNLOADS` to a share path such as `/mnt/user/media/downloads`. Mount that same
path into your Sonarr container.

**Synology.** Container Manager → Project → *Create* from the compose file. Two gotchas: put the project
somewhere under `/volume1/docker/`, and check that ports `5055`, `5099` and `18888` do not collide with
DSM's own services — DSM is liberal with high ports. Set `KRAUTWATCH_DOWNLOADS` to a
`/volume1/...` path, not a `/docker/...` one.

**Raspberry Pi.** Works on `arm64` with a 64-bit OS. Seven containers plus Postgres on a 4 GB Pi 4 is
tight but fine when idle; ffmpeg remuxing an HLS stream is the demanding part, and remuxing is a copy
(`-c copy`), not a re-encode, so it is IO-bound rather than CPU-bound. Put the download directory on an
SSD, not the SD card.

---

## 11. Upgrading

```bash
cd ~/krautwatch
curl -LO https://github.com/Chrison-dev/Krautwatch/releases/latest/download/docker-compose.yaml
# diff env.example against your .env for new keys
docker compose pull && docker compose up -d
```

`migrator` applies schema changes on the way up, and every other service waits for it, so there is no
manual migration step. **Back up first** (§12) — while this is pre-1.0, treat every upgrade as one-way.

Pin a version instead of tracking `latest` by editing the image tags in `.env`.

---

## 12. Backup and restore

Two things matter:

1. **The database** — your catalog, settings, `*arr` instances and show mappings.
2. **`.env`** — it holds `POSTGRES_PASSWORD`. Lose it and the volume is unreadable.

```bash
# back up
docker compose exec -T postgres pg_dump -U postgres krautwatch | gzip > krautwatch-$(date +%F).sql.gz
cp .env krautwatch-env-$(date +%F).bak

# restore into a fresh stack
gunzip -c krautwatch-2026-08-09.sql.gz | docker compose exec -T postgres psql -U postgres krautwatch
```

> Unless you use secret references (§7), **the dump contains your API keys in plain text**. Store it
> accordingly.

`docker compose down` keeps the `krautwatch-pgdata` volume. `docker compose down -v` **deletes your
catalog**.

---

## 13. Troubleshooting

**Is it alive?** Every service exposes `/health` and `/alive` on port 8080 internally, and the images
ship `curl`:

```bash
docker compose exec newznab curl -fsS localhost:8080/health
docker compose exec agent-downloader curl -fsS localhost:8080/health
docker compose logs -f agent-downloader
```

The Aspire dashboard on `:18888` collects logs and traces from every service in one place, which is
usually faster than chasing `docker compose logs` per container.

### Sonarr says "No files are eligible for import"

The single most common failure, and almost always storage rather than Krautwatch:

1. Confirm the file exists: `docker compose exec agent-downloader ls -la /downloads`.
2. Confirm Sonarr sees the **same** path: `docker exec sonarr ls -la /downloads`.
3. If the paths differ, fix the mounts (§3) rather than adding a Sonarr remote-path mapping.
4. If Sonarr sees it but cannot move it, compare ownership — `PUID`/`PGID` mismatch.

### The indexer returns nothing

- `curl "http://<host>:5055/api?t=caps"` — no answer means wrong host/port, not a search problem.
- Search returns results but Sonarr grabs nothing → usually series-type vs release-naming (§5), and
  usually fixed by configuring TheTVDB (§6).
- Everything returns empty and the logs mention resolution timing out → raise the wait in
  **Settings → Search**, or check the agents can reach the broadcasters.

### Downloads fail immediately

- Geo-restricted content with no proxy configured fails fast on purpose (§8).
- ZDF changing its API auth key breaks ZDF crawling until Krautwatch is updated
  ([#13](https://github.com/Chrison-dev/Krautwatch/issues/13)).

### A settings row shows an API-key problem

A secret reference that does not resolve in that container (§7). The message names the variable or path.

---

## 14. Known limitations

Worth knowing before you commit to this:

- **A daily series cannot be searched from Sonarr** ([#95](https://github.com/Chrison-dev/Krautwatch/issues/95))
  — the single biggest limitation today, since most German public TV is dated.
- **Subtitles are German-only and best-effort.** Where a broadcaster publishes a WebVTT track it is
  written beside the video as `{video}.de.vtt`, which Plex, Jellyfin and Sonarr pick up unaided. Not
  every programme has one, and a subtitle that fails to fetch never fails the video.
- **No OIDC** ([#48](https://github.com/Chrison-dev/Krautwatch/issues/48)).
- **The download queue ignores `MaxConcurrentDownloads`** and has no priority ordering
  ([#51](https://github.com/Chrison-dev/Krautwatch/issues/51)).
- **The HLS/ffmpeg path has no stall guard** — a wedged remux will not time out on its own. Only the
  progressive-MP4 path recovers.
- **SABnzbd queue reports `mb` and `timeleft` as zero.** Cosmetic; Sonarr tracks by `nzo_id`.
- **No first-run wizard yet** ([#54](https://github.com/Chrison-dev/Krautwatch/issues/54)) — which is why
  this document exists.

Krautwatch downloads freely available content from German public broadcasters' own official APIs, for
personal offline use, circumventing no DRM. Respect your local law and the broadcasters' terms;
geo-restriction routing is intended for licence-fee payers reaching content they are already entitled to.
