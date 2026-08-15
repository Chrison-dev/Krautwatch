# Issue and PR writing style

How issues and pull requests are written here, by humans and by AI tools alike.
Adapted from [Fallout-build/Fallout](https://github.com/Fallout-build/Fallout/blob/main/docs/agents/issue-and-pr-style.md),
which is the fuller version.

Goal: terse and scannable. A reader should get the point on the first screen, on
a phone, without scrolling.

**Base branch: `develop`.** It is the default, so `gh pr create` targets it
without `--base`; pass `--base` explicitly anyway when you mean `main` or a
support line, so the intent is on the record. See
[branching-and-release.md](../branching-and-release.md).

## PR titles

A PR title is a changelog line. It is read months later, out of context, in
[the release notes](../../.github/release.yml) — so write it as a sentence
someone can understand there.

- **Imperative sentence, sentence case, no trailing period.**
  "Publish images for amd64 and arm64".
- **No `feat(scope):` prefix.** The category label already says whether it is a
  feature or a fix, and the release notes group by it. A prefix repeats the
  section heading it sits under.
- **No bare issue numbers or part markers.** `(#45 · 1/3)` means nothing to a
  reader of the notes. Put that in the body.
- **Say what changed for the user**, not which files moved.

| Instead of… | Write… |
| --- | --- |
| `feat(indexer): Newznab indexer host + Application/Indexing slice` | Expose a Newznab indexer for Sonarr and Prowlarr |
| `fix: make an *arr grab actually download and reach import` | Make a Sonarr grab download and reach import |
| `chore(deps): clear all five package advisories, .NET 10 servicing patches, and test-tooling majors` | Clear all five package advisories |
| `feat(5a): Postgres + durable Wolverine; remove dead TickerQ pipeline` | Move persistence to Postgres and messaging to durable Wolverine |

## Labels

Label the PR **when creating it**, in the same `gh pr create --label …` call.
[`.github/release.yml`](../../.github/release.yml) is the source of truth for the
categories: `enhancement`, `bug`, `security`, `documentation`,
`breaking-change`, `dependencies`, `skip-changelog`. An unlabelled PR falls
through to "Other Changes".

Two traps worth knowing:

- `dependencies` and `skip-changelog` are **excluded** from the notes. A PR
  carrying `security` *and* `dependencies` disappears, because exclusion beats
  category. A dependency bump that fixes a CVE gets `security` alone.
- Build and CI work is usually `skip-changelog`, but not always. Multi-arch
  images were build work that users could feel, so they were `enhancement`.

## PR description shape

```markdown
<one line: what this PR does and why>

### What changed
- <short bullet, not a file-by-file narration>

### Why
<only if it is not obvious from the summary>

### Verification
<what you actually ran>

Closes #<issue>
```

Drop any section that does not apply rather than padding it. Match length to
substance: a one-line fix gets a one-line description.

- **Link, don't recap.** Reference issues, PRs and code (`path/to/file.cs:42`)
  instead of pasting them.
- **Keep what the reader cannot get elsewhere.** The diff shows which files
  changed. The linked issue holds the requirement. Verification and follow-ups
  are the parts only you know — those earn their place.
- **Describe outcomes, not your process.** What changed and why it matters, not
  the journey.
- **Write for non-native English readers.** Short sentences, one idea each,
  plain words over idiom. "affects fewer consumers", not "blast radius".
- **No filler.** No preamble, no restating the title, no marketing adjectives.
