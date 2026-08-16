# GitFlow adoption + the whole pipeline in Fallout

**Date:** 2026-08-16
**Status:** agreed, being implemented

## Why

Krautwatch has shipped five tagged releases off a single `main`. That worked while every change
went straight out, but it has no answer to two questions the project now has:

- **"What is in production?"** — `main` is simultaneously the trunk and the released line, so the
  answer is "whatever the last tag pointed at", which is not a ref you can diff against.
- **"How do testers get the next thing before it releases?"** — they can't. The only artefacts are
  the `v*`-tagged images.

The [Fallout VS Code extension repo](https://github.com/Fallout-build/Fallout.Extensions.VSCode)
already runs GitFlow with a rolling preview channel and documents it in three files
(`branching-and-release.md` / `ci.md` / `releasing.md`). Adopt the same model here, with the same
governing principle: **the build is defined in C#, not in YAML.**

## The model

| Branch | Purpose | Tagged |
|---|---|---|
| `develop` | Integration trunk, **default branch**. Every push publishes `:edge` images. | No |
| `main` | Production. Only `release/*` and `hotfix/*` merges land here. | **Yes** (`v*`) |
| `release/*` | Stabilisation window cut from `develop`. | No |
| `hotfix/*` | Urgent production fix cut from `main`. | No |
| `support/vX.Y` | Maintenance line for an older release. **None today.** | Yes |
| `feat/*`, `fix/*`, `chore/*`, `docs/*` | Working branches — the prefixes this repo already uses. | No |

`support/*` is wired into the gate and the release guard from day one because it costs one string
each, and the alternative is discovering it isn't wired at the exact moment a user needs an old
line patched.

## What changes in the build

Everything below is a Fallout target or a `[GitHubActions]` attribute. **No hand-written YAML** —
the four existing workflows are already generated, and the fifth will be too.

1. **`build/Build.CI.GitHubActions.cs`** (new) — the `[GitHubActions]` attributes move out of
   `Build.cs`, which goes back to being about targets. Branch names become `const string` fields so
   the gate, the edge channel and the release guard cannot drift apart.
2. **The gate widens.** `OnPushBranches = [develop, main]`,
   `OnPullRequestBranches = [develop, main, release/*, hotfix/*, support/*]`. No path exclusions:
   a required check that is skipped for docs-only PRs blocks them forever unless a second
   "skip" workflow reports the same context, and that workflow would have to be hand-written.
3. **A `publish-edge` workflow** on every push to `develop`, invoking a new `PushEdge` target that
   pushes the six multi-arch images to GHCR under `:edge`. Docs-only pushes are excluded (safe
   here — it is not a required check). Concurrency **queues rather than cancels**: a cancelled push
   can leave the registry holding a half-written manifest.
4. **A release-ref guard.** `GitHubRelease` refuses to publish unless the tag is reachable from
   `main` or a `support/*` branch. Under GitFlow the trunk is never tagged for release, and the
   failure mode without this is silent — a `v*` tag on `develop` publishes a real release from
   unstabilised code.

`EffectiveTag` already resolves a tag build to its `v`-stripped tag and everything else to `dev`;
the edge target overrides it to `edge` rather than teaching that property about branches.

### Versioning stays manual

No Nerdbank.GitVersioning. The extension repo needs it because marketplace versions must increase
monotonically on every preview build; Krautwatch's preview channel is a single rolling `:edge` tag
with no number to compute. `v*` tags stay hand-picked and semver-ish, and the images take the tag.

## Docs

Three files mirroring the extension repo, because the split has earned itself there:

- `docs/branching-and-release.md` — the branch model, where work lands, protection, merge methods.
- `docs/ci.md` — what runs, when, and why each workflow is shaped that way.
- `docs/releasing.md` — channels and the runbook for every kind of release.

Plus pointers from `README.md`, `CLAUDE.md` and `docs/agents/issue-and-pr-style.md`, all of which
currently imply PRs target `main`.

## GitHub-side setup

- `develop` created from `main` and made the default branch.
- Protection on `develop`, `main` and `support/*`: PR required, `ubuntu-latest` required, linear
  history, no force-push or deletion, **admins exempt** (the escape hatch for a stuck check).
- A ruleset blocking non-admin creation/deletion/update of `v*` tags — every release channel is
  tag-triggered, so an accidental tag is an accidental release.

## Then: cut v0.3.0

The first release through the new path. `develop` → `main`, tag `v0.3.0` on `main`,
which fires `publish-ghcr` and `publish-release`. Minor bump because subtitle download (#20) is a
new user-facing capability; the rest of the batch is fixes.

## Outcome (2026-08-16)

Done, and proven rather than assumed:

- The trunk push published all six multi-arch images to `:edge` in **8m30s**; the docs-only push
  that followed correctly published nothing, so the path filter works.
- **v0.3.0** went out with `:0.3.0` images (amd64 + arm64) and a release carrying
  `docker-compose.yaml` + `env.example`.
- Rulesets applied to `develop`/`main`/`support/*` and to `v*` tags; default branch switched; merge
  commits disabled.

**One thing the first release taught us.** Its notes were missing #101 and #103. GitHub's *Rebase
and merge* rewrites commits even for a pure fast-forward, so `main`'s copies were associated with
the release PR (`skip-changelog`) instead of the PRs that did the work, and the generated notes
dropped them. Fixed by advancing `main` with a **fast-forward push** instead — documented in
[branching-and-release.md](../branching-and-release.md#merging) — after a one-off realignment of
`develop` onto `main`'s SHAs so the two share ancestry again. v0.3.0's notes were amended by hand.

The lesson generalises: **anything that rewrites commits between `develop` and `main` costs the
release its changelog**, because the changelog is derived from the commit→PR link and nothing else.
