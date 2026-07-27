# 2026-07-28 — Query-driven search: resolve `t=tvsearch` on demand (#58)

**Status:** planned · **Milestone:** ARD and KiKA indexer · **Implements:** [DR-011](../architecture/DR-011-search-driven-indexing.md)

`SearchReleasesHandler` reads only `IEpisodeRepository`, and the crawl list is three hardcoded seed shows
(`extra 3`, `Die Biene Maja`, `heute-show`). So Sonarr searching for anything else gets an empty feed —
indistinguishable from "not available in the Mediathek". DR-011 makes on-demand resolution the target,
because that is what lets Krautwatch work with **zero** `*arr` configuration.

## Where resolution runs, and why that matters

The Newznab host currently registers **no broadcaster crawlers** — only the agents do
(`AddArdCrawler`/`AddZdfCrawler` are absent from `Api/NewznabIndexerApi/Program.cs`). Live resolution
therefore needs one of:

1. **In-process in the API host** — register the crawlers there and call the port directly.
2. **Dispatch to an agent over the durable bus** and poll for completion.

**Decision: (1).** Option 2 puts a message round-trip plus polling inside Sonarr's HTTP request, for a
result that is needed synchronously — far more latency and machinery than the problem warrants. This makes
the API host run an IO-driven Action, which is the same narrow DR-009 deviation already recorded for
`TestArrConnection`: Actions are *supposed* to live on agents, but a synchronous request/response cannot
wait on a bus. Recorded here rather than discovered later.

## Slice isolation forces a small duplication

The obvious move — call `Crawling`'s `CrawlShowHandler` from `Indexing` — is **forbidden** by the
`Slice_does_not_depend_on_sibling_slices` architecture test, and rightly so. `Indexing` gets its own Action
using the `IBroadcasterCrawler` port and `IEpisodeRepository` directly. The "crawl then upsert" shape
repeats; that is the intended cost of slice isolation, not an accident to refactor away.

## Shape

### 1. Resolution cache (`ResolvedQuery`)

Without a marker of "we already looked", every repeat search re-crawls ARD. Sonarr retries the *same*
failing query on a schedule, so **negative caching matters more than positive** here.

- `ResolvedQuery` entity: normalised query text (key), `LastAttemptedAt`, `ResultCount`, `ProvidersTried`.
- TTLs, configurable: **positive 6h**, **negative 45m**. Rationale: public-TV episodes appear on
  broadcast schedules, not continuously, so 6h is ample; 45m keeps a mistyped or genuinely-absent show from
  hammering ARD every RSS-Sync cycle while still recovering within an hour of the show appearing.
- Migration `AddResolvedQueries`.

### 2. On-demand resolution Action (`Application/Indexing`)

- Fan out to **all** registered crawlers concurrently (ard, kika, zdf) under one shared deadline, since we
  cannot know which broadcaster carries a title.
- **Bound the wait, not the crawl.** The request waits a configurable deadline (default 8s) and then
  serves whatever has landed — but the crawl **keeps running in the background** to completion, so the next
  call (search or RSS) gets the full set. This is the key decision: abandoning a half-finished crawl would
  throw away work already paid for in ARD round-trips, and would leave the cache permanently partial.
- **Therefore the crawl must not use the request's CancellationToken.** It runs on a queue drained by a
  hosted service, tied to the *host* lifetime (`ApplicationStopping`) with its own longer budget. The
  request merely awaits a completion signal up to its deadline. Getting this wrong — passing the request
  token through — would silently cancel every crawl the instant the response is written.
- **Coalesce** concurrent identical queries: the in-flight table means a Sonarr library refresh issuing the
  same query twice crawls once, and a second caller simply waits on the first one's signal. Per-process
  only — several API replicas would each crawl once, which is acceptable and not worth distributed locking.
- **Outbound politeness:** cap concurrent background resolutions so a library refresh cannot become a crawl
  storm against ARD.

### 3. Wire into search

`SearchReleasesHandler`: on a DB miss for a non-empty `q`, and if the resolution cache is stale, resolve →
upsert → re-read → serve in the same response. The RSS path (`q` empty) is untouched — per DR-011 it keeps
serving the standing crawl list.

## Decisions

**Everything is configurable** under `Indexing:OnDemandResolution` — the whole point is that an operator on
a slow link or a fast LAN can tune it without a rebuild:

| Setting | Default | Purpose |
|---|---|---|
| `Enabled` | `true` | Kill switch |
| `RequestDeadline` | `00:00:08` | How long a search waits before answering |
| `CrawlTimeout` | `00:02:00` | Budget for the background crawl, independent of the request |
| `PositiveTtl` | `06:00:00` | How long a successful resolution is trusted |
| `NegativeTtl` | `00:45:00` | How long an empty result is trusted |
| `MaxConcurrentResolutions` | `2` | Politeness cap on outbound crawling |

**Only `q` searches resolve, never the RSS feed.** RSS-Sync polls constantly with no query; resolving there
would mean crawling on a timer for no defined target.

**A miss still returns 200 with an empty feed**, never an error. Sonarr treats indexer errors as an
availability problem and will disable the indexer after repeated failures — "no results" and "broken" must
stay distinguishable.

**Deliberately out of scope: adding resolved shows to the standing crawl list.** It is tempting (a show you
searched for should keep producing episodes in RSS), but it grows the crawl list without bound and needs its
own retention policy. Filed separately rather than smuggled in.

## Tests

- Resolution cache: fresh positive suppresses a crawl; stale positive re-crawls; fresh **negative**
  suppresses (the Sonarr-retry case); stale negative re-crawls.
- Coalescing: two concurrent identical queries produce **one** crawl.
- Deadline: a crawler that never returns does not hang the search; whatever is in the DB is served.
- **The background crawl survives the response.** A crawl that finishes *after* the request deadline still
  persists its episodes, so the next call sees the full set — this is the behaviour that would break if the
  request token were threaded into the crawl, and it is the single most important test here.
- A crawler throwing does not fail the whole search — the others still contribute.
- RSS path (`q` empty) never triggers resolution.
- A miss returns an empty result rather than throwing.
