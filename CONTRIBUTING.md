# Contributing

Krautwatch is a self-hosted Newznab indexer and SABnzbd download client for German public
broadcasters. Contributions are welcome — especially **new broadcasters**, which have a documented
shape of their own: [docs/adding-a-broadcaster.md](docs/adding-a-broadcaster.md).

## Getting set up

```bash
dotnet tool restore                          # Fallout + the Aspire CLI are pinned local tools
./build.sh Test                              # restore + compile + unit/architecture tests
dotnet run --project src/Presentation/AppHost   # the whole fleet, via Aspire
```

You need **.NET 10**, **Docker running** (the repository tests use a real Postgres container via
Testcontainers) and **ffmpeg on PATH** if you're working on downloads.

`./build.sh TestLive` runs the tests that hit ARD and ZDF for real. They're excluded from the CI
gate because external APIs drift and rate-limit — run them yourself when you touch a crawler.

## Where work lands

The repo runs **GitFlow**: `develop` is the default branch and the integration trunk, `main` is what
is released.

```bash
git switch develop && git pull --ff-only
git switch -c feat/my-change
./build.sh Test
gh pr create --base develop --label enhancement
```

The full model — where a fix belongs, how releases are cut, what CI does at each step — is in
[docs/branching-and-release.md](docs/branching-and-release.md), [docs/ci.md](docs/ci.md) and
[docs/releasing.md](docs/releasing.md).

## Pull requests

**A PR title is a changelog line.** It appears verbatim in the release notes, months later, out of
context — so write an imperative sentence: "Serve the SABnzbd surface on /api", not
`fix(api): sab endpoint`. No `feat(scope):` prefixes, no bare issue numbers. Full guidance in
[docs/agents/issue-and-pr-style.md](docs/agents/issue-and-pr-style.md).

**Label the PR when you create it**, in the same `gh pr create --label …` call — the labels *are*
the changelog. One category from [`.github/release.yml`](.github/release.yml): `enhancement`, `bug`,
`breaking-change`, `security`, `documentation`, `dependencies`, or `skip-changelog` for
housekeeping.

Note that `dependencies` and `skip-changelog` are both excluded from the notes, so a PR carrying
`security` **and** `dependencies` vanishes entirely — a CVE-fixing bump gets `security` alone.

## What the reviewer will check

- **`./build.sh Test` is green**, architecture tests included. Four ArchUnitNET rules enforce the
  hexagon: Domain depends on nothing, Application only on Domain, Infrastructure never on
  Presentation, and no slice depends on a sibling slice.
- **The layering is respected.** Ports live in `Domain/Interfaces`; adapters in `Infrastructure`;
  use-cases as vertical slices in `Application` with the CQRS/A split marked by banner comments
  inside each file. [`CLAUDE.md`](CLAUDE.md) is the working map of the layout, and
  `docs/architecture/` holds the decision records — **DR-009, DR-010 and DR-011 are current**; read
  them before a structural change.
- **Generated files aren't hand-edited.** `.github/workflows/*.yml` comes from the
  `[GitHubActions]` attributes in `build/Build.CI.GitHubActions.cs`, and the compose file comes from
  the Aspire AppHost. Editing either by hand is silently undone.
- **Anything user-visible is documented** in the README, and anything structural gets a plan in
  `docs/plans/` first — the convention is `YYYY-MM-DD - <title>.md`, written before implementation.

## Adding a broadcaster

The catalog is built entirely by per-broadcaster crawlers behind one port, `IBroadcasterCrawler`, so
adding ORF, SRF or arte is a bounded unit of work: an HTTP client, an adapter, an agent host, and
four registrations. The walkthrough, with the traps called out, is in
**[docs/adding-a-broadcaster.md](docs/adding-a-broadcaster.md)**.

## Reporting issues

Bugs and feature requests both belong in [GitHub issues](https://github.com/Chrison-dev/Krautwatch/issues).
For a crawl or download failure, the useful details are: the broadcaster, the show, what Sonarr
asked for, and the agent's log lines around the failure. For a geo-restricted asset, say whether
`Download:ProxyUrl` was configured — that's the difference between a bug and the documented
fail-fast.

## Legal

Krautwatch downloads freely available content from German public broadcasters' own official APIs for
personal, offline use, and circumvents no DRM. Contributions that add DRM circumvention, scrape
paywalled or commercial catalogs, or bypass a broadcaster's access controls will not be merged.
