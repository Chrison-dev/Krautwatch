# DR-011 — Search-Driven Indexing (the monitored list is not the work-list)

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-07-28 |
| **Deciders** | Christian |
| **Refines** | DR-010 (arr-indexer direction) — specifically retracts its **Reach-back** clause |

## Context

DR-010 established that Krautwatch is a Sonarr/Radarr **indexer + download client**, and that stands.
But one clause went further than the integration requires:

> **Reach-back:** poll each configured Sonarr/Radarr instance for its `monitored` series list; that is
> the crawl work-list.

That made the monitored list load-bearing: #6 was scoped as "the crawl work-list", #12 (RSS for
monitored shows) was blocked behind it, and #4/#5 existed largely to make it possible. Before building
it, we checked how the two comparable projects solve the same problem.

### How the comparables actually do it

**MediathekArr** keeps no catalog. It is a **live search proxy** — "pretending to be a usenet indexer,
but actually just fetching and parsing search results from **MediathekViewWeb**". TVDB supplies
metadata, and the effort goes into an "advanced filter and matching system for TV shows, seasons and
episodes" to survive "the horrendous lack of consistency and metadata in ARD/ZDF Mediatheken".

**RundfunkArr** uses **curated configuration**: `data/shows.json` (show metadata incl. `tvdbId`, german
name, aliases, episodes) plus `data/rulesets.json` (per-show Mediathek topic, duration filters, regex
for season/episode extraction, matching strategy). Shows are added by hand or community pull request.
SQLite is cache and download history only.

**Neither reads Sonarr's monitored series via the Sonarr API.** Both are pure Newznab providers.

### Why they don't need to

The Newznab contract is **pull-by-query, not push-by-work-list**. Sonarr already knows what it monitors
and issues `t=tvsearch&q=…&tvdbid=…&season=&ep=` for precisely what it wants. An indexer is not
supposed to know the monitored list — that is the client's job.

The reason the question arose for us is a decision we made, not a property of being an `*arr` indexer:
DR-010 **de-emphasised MediathekView in favour of direct ARD/ZDF APIs**. MediathekViewWeb is
effectively a complete dump, so MediathekArr gets breadth for free. Our ARD crawler must walk A-Z
catalog → show page → episode list → item page, so it has to be told *which* shows to walk.
RundfunkArr has the identical constraint and answers it with human curation.

**The work-list problem is self-inflicted by the direct-API choice.**

## Decision

**Retract the reach-back clause.** The monitored list is not the crawl work-list. Instead:

1. **Search is query-driven.** `t=search`/`t=tvsearch` resolves against the broadcaster on demand and
   caches the result, so any show Sonarr asks for works without having been pre-registered. This is
   what makes Krautwatch behave like an indexer rather than a catalog with a search box.
2. **A standing crawl list exists only to feed the RSS feed.** RSS-Sync needs a recent-releases list, so
   *something* must be crawled proactively. That list is configuration (today `CrawlOptions.Targets`),
   optionally **pre-warmed** from a Sonarr/Radarr monitored list where one is configured.
3. **Reach-back becomes an optional convenience, never a prerequisite.** No feature may depend on a
   configured `*arr` instance to function.
4. **Matching is where the effort goes.** Both comparables concentrate there, and it decides whether
   Sonarr can match anything at all. Our equivalents are `EpisodeNumbering.Parse`, `SeriesType`, and the
   currently-unset `Show.TvdbId`.

### Why not reach-back as the primary mechanism

- **It inverts the dependency.** Holding Sonarr credentials and calling back into it is a bidirectional
  coupling neither comparable has, and it is the reason #4 and #48 became prerequisites for basic
  indexing.
- **It does not satisfy search.** An interactive search for a show that is unmonitored, or monitored but
  not yet crawled, returns nothing. A query-driven path is needed regardless — which demotes reach-back
  to an optimisation by definition.

## Consequences

- ✅ Indexing works with **zero** `*arr` configuration, which is how a Newznab indexer should behave.
- ✅ Unblocks #12: the RSS feed can serve the standing crawl list without waiting on #6.
- ✅ #4/#5 stop being prerequisites for core function; they remain worthwhile for pre-warming and for
  the setup experience (#54).
- ⚠️ On-demand search puts broadcaster latency in Sonarr's request path. The ARD flow is multi-hop
  (A-Z widget → show page → episodes → item page), so caching and a sane timeout are mandatory, not
  optional polish.
- ⚠️ Rate-limiting/politeness toward ARD/ZDF becomes a real concern once searches hit them live.
- ⚠️ **#49 (delete the MediathekView subsystem) is now on hold.** That filmliste parser is precisely the
  full-catalog source MediathekArr depends on; it is the mechanism for breadth without per-show
  curation. Decide the search model before deleting it. (The `SharpCompress` advisory that motivated #49
  was resolved by a version bump, so there is no security pressure to remove it.)

  > **Hold lifted 2026-08-09 — the condition above was met.** The search model *was* decided and shipped:
  > #58 implemented on-demand resolution against the broadcasters' own APIs, so breadth no longer depends
  > on a full-catalog dump. The subsystem was removed in #49. Note it had already decayed past being a
  > usable fallback — it was never wired into any host, `ICatalogProvider` had no live implementation but
  > itself, and reviving it would have meant rebuilding the wiring regardless. Reviving the *approach*
  > remains possible; this decision record and git history are the specification.
- ⚠️ DR-010 remains correct on everything else — product direction, the Newznab/SABnzbd surfaces, the
  provider/port abstraction, and the UI shrinking to configuration.

## Evidence and its limits

Both READMEs were read directly (see Sources). Two gaps worth recording honestly:

- MediathekArr's README does not *explicitly* state live-versus-cached; "fetching and parsing search
  results" strongly implies live, but it is an inference.
- **Neither** project documents RSS feed support, so how they serve RSS-Sync is unknown — which is
  precisely the part that still requires a standing crawl. Our RSS design cannot be copied from either
  and has to be reasoned out.

## Sources

- <https://github.com/PCJones/MediathekArr> and its README
- <https://github.com/rundfunkarr/rundfunkarr>
- <https://github.com/awesome-selfhosted/awesome-selfhosted-data/issues/1943>
