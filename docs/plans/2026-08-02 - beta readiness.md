# 2026-08-02 — Beta readiness

**Status:** ✅ **shipped as v0.1.1** (2026-08-02). Branch merged via PR #24; the release publishes
multi-arch images to GHCR plus `docker-compose.yaml` + `env.example` as assets.

**✅ Complete as of 2026-08-09** — Sonarr's import is proven. See §"The import proof" below.

*(Reviewed 2026-08-09 — doc-sync pass. Kept as the record of what the beta covered.)*

Where the first beta stands, and what is left. The engineering detail lives in the commit messages;
this is the state that is not otherwise written down anywhere.

## What works end to end

| Step | Status |
|---|---|
| Sonarr searches → finds our releases | ✅ PR #70 |
| Sonarr grabs → valid NZB accepted | ✅ PR #71 |
| NZB → our SABnzbd → job created | ✅ PR #71 |
| Download completes, correct release filename | ✅ PR #71 |
| Sonarr resolves the path and attempts import | ✅ PR #71 |
| **Sonarr actually imports** | ✅ proven 2026-08-09 — see below |
| Whole stack installable from compose | ✅ this branch |

Every step is now proven against a real Sonarr 4.0.19, including the import.

## Remaining before the beta ships

1. ~~**Create the GitHub environments**~~ — ✅ done; `ghcr` publishes on every release.
2. ~~**Confirm `RegistryNamespace`**~~ — ✅ `chrison-dev`, as published in v0.1.0 / v0.1.1.
3. ~~**Raise the PR** for this branch~~ — ✅ merged (#24), plus follow-ups #76 / #77 / #78 hardening the
   release job (wait for this commit's image build, PR-title changelog lines, `curl` in the images).
4. ✅ **Prove the import** — done 2026-08-09.

## The import proof

Run against the **released v0.2.1 images**, with Sonarr 4.0.19.2979 in the same compose project sharing
the download directory at an identical host *and* container path (`/downloads`), so no remote-path
mapping was involved.

`heute-show.2026-06-05.GERMAN.1080p.WEB.h264` — 1.44 GB, ZDF, not geo-restricted:

| Step | Result |
|---|---|
| Grab accepted by our SABnzbd surface | ✅ `nzo_id` returned |
| Downloader fetched the stream | ✅ 1.3 GB written to `{release}/{release}.mp4` |
| Sonarr's queue saw the item | ✅ `completed` / `importPending`, matching `nzo_id` |
| Sonarr matched the file | ✅ `heute-show (DE)` 2026×17, air date 2026-06-05, WEBDL-1080p, no rejections |
| **Sonarr imported it** | ✅ history `downloadFolderImported`; episode `hasFile: true` |
| Sonarr cleaned up `/downloads` | ✅ moved into the library, source folder emptied |

**Two blockers were found and filed on the way**, both of which stop a fresh install cold:

- **#96** — the SABnzbd surface is at `/sabnzbd/api`, not `/api`. Following our own docs, the download
  client cannot be added at all. Worked around here with URL Base `/sabnzbd`.
- **#95** — a **daily** series cannot be searched: Sonarr sends the air date as `season`/`ep`, and our
  filter drops every dated episode. This is why the grab had to be pushed by hand rather than through
  Sonarr's search. It affects most German public TV.

So the *plumbing* is proven end to end; #95 is what still stands between that and an operator getting
the same result unaided.

## Running state (2026-08-02 — ⚠️ expired, do not trust)

As of 2026-08-09 the Docker daemon is not even running, so none of the below is live. Reconstruct with
`docker compose up -d` from a release's compose file rather than expecting these to be there. Recorded
only so the test data is recognisable if it does resurface:

- **Compose stack** was up from `.artifacts/compose/` — 7 services. `newznab` :5055, `web` :5099,
  Aspire dashboard :18888. Secrets are in `.artifacts/compose/.env`, which is gitignored: the API key
  and Postgres password only exist there, so read them from that file rather than regenerating — if
  that file is gone, the old stack's data is unreachable and regenerating is the only option.
- **A stray `postgres-cyncxsrx` container** survived from the earlier Aspire dev fleet, whose parent
  process was long gone. It held the dev catalog used for the PR #70/#71 demos — 23 crawled shows,
  the `extra 3` mapping at 5 picks, 65 RundfunkArr hints. Harmless, and nothing depends on it.
  Safe to remove once that test data is no longer wanted.

## Configuration left on the operator's Sonarr (192.168.179.153)

Both were created during testing and both point at a **laptop LAN address that has already changed
once mid-session** (192.168.178.85 → 10.20.208.221). They will need repointing at wherever
Krautwatch actually gets deployed, or deleting.

- Indexer id **17**, "Krautwatch (dev)" — RSS and automatic search deliberately **off**, interactive
  only, so nothing grabs unattended while the integration is still being worked on.
- Download client id **2**, "Krautwatch (dev)" (SABnzbd), category `tv`.
- Five test series added: heute-show (234791), Extra 3 (255986), both *Maya the Bee*
  (73518 / 266275), ZDF Magazin Royale (390284).

## Known gaps, deliberately not addressed yet

- `ByAbsoluteEpisodeNumber` matching (4 shows in RundfunkArr's set).
- A genuinely `daily` Sonarr series is untested — we emit `SxxEyy` whenever TVDB supplies numbers.
- The ffmpeg/HLS download path has no stall guard; only the progressive-MP4 path does.
- SABnzbd queue reports `mb`/`timeleft` as zero. Cosmetic — Sonarr tracks by `nzo_id`.
- `NU1608` fires on every restore (still true 2026-08-09, on 5 projects): `Scrutor.Extensions.HttpClient`
  5.0.1 caps `Microsoft.Extensions.Http` below 10.0.0, but 10.0.10 resolves. It arrives **transitively via
  `TvdbClient` 4.7.12** — nothing in this repo references Scrutor directly, so the fix is a net10 bump of
  `TvdbClient` (first-party), which clears it repo-wide.
