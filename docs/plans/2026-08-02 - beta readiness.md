# 2026-08-02 — Beta readiness

**Status:** in progress · branch `feat/compose-and-image-publishing` (1 commit, unpushed, no PR yet)

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

1. **Create the GitHub environments** — `ghcr` and `dockerhub`, each holding `REGISTRY_USER` and
   `REGISTRY_PASSWORD`. A PAT with `write:packages` works for GHCR. Nothing can publish until these
   exist; the workflows are generated and waiting.
2. **Confirm `RegistryNamespace`** — defaults to `chrison-dev` in `build/Build.Publish.cs`.
3. **Raise the PR** for this branch.
4. **Prove the import**, which needs Krautwatch's downloader and Sonarr to share a filesystem. Until
   then the last hop is configuration-shaped rather than demonstrated.

## Running state (2026-08-02, will not survive a reboot)

- **Compose stack up** from `.artifacts/compose/` — 7 services. `newznab` :5055, `web` :5099,
  Aspire dashboard :18888. Secrets are in `.artifacts/compose/.env`, which is gitignored: the API key
  and Postgres password only exist there, so read them from that file rather than regenerating.
- **A stray `postgres-cyncxsrx` container** survives from the earlier Aspire dev fleet, whose parent
  process is long gone. It holds the dev catalog used for the PR #70/#71 demos — 23 crawled shows,
  the `extra 3` mapping at 5 picks, 65 RundfunkArr hints. Harmless, but it is not the compose
  database and nothing depends on it. Safe to remove once that test data is no longer wanted.

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
- `NU1608` fires on every restore: `Scrutor.Extensions.HttpClient` 5.0.1 caps
  `Microsoft.Extensions.Http` below 10.0.0. First-party package; a net10 bump clears it repo-wide.
