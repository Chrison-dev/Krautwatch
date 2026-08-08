# 2026-08-02 — Beta readiness

**Status:** ✅ **shipped as v0.1.1** (2026-08-02). Branch merged via PR #24; the release publishes
multi-arch images to GHCR plus `docker-compose.yaml` + `env.example` as assets.

**One item outstanding:** proving Sonarr's import (item 4 below). Everything else on this plan is done.

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
| **Sonarr actually imports** | ❌ needs shared storage — the compose work below is what makes it possible |
| Whole stack installable from compose | ✅ this branch |

The import step has never been proven. Everything up to it has, against a real Sonarr 4.0.19.

## Remaining before the beta ships

1. ~~**Create the GitHub environments**~~ — ✅ done; `ghcr` publishes on every release.
2. ~~**Confirm `RegistryNamespace`**~~ — ✅ `chrison-dev`, as published in v0.1.0 / v0.1.1.
3. ~~**Raise the PR** for this branch~~ — ✅ merged (#24), plus follow-ups #76 / #77 / #78 hardening the
   release job (wait for this commit's image build, PR-title changelog lines, `curl` in the images).
4. ⬜ **Prove the import**, which needs Krautwatch's downloader and Sonarr to share a filesystem. Until
   then the last hop is configuration-shaped rather than demonstrated. **Still the top open item.**

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
