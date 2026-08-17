# Pre-warm the standing crawl list from `*arr` monitored series (#6)

**Date:** 2026-08-17
**Status:** agreed, being implemented

## Why, and why it is only a pre-warm

Per [DR-011](../architecture/DR-011-search-driven-indexing.md) the Sonarr monitored list is **not** the
crawl work-list — search resolves on demand, so nothing depends on reach-back. What the monitored list
is genuinely good for is the *standing* list: the RSS feed should carry recent episodes of the shows
the operator actually watches, without them hand-curating `Crawl:Targets` the way RundfunkArr makes you
curate `shows.json`.

So this is **opt-in, additive, and allowed to fail**. Every failure mode degrades to exactly today's
behaviour.

## The mapping problem is mostly already solved

The issue calls mapping an `*arr` series to a broadcaster show "the hard part". Most of it exists:
`ShowMapping` maps **`TvdbId` → `ShowId`**, and our show ids are `{providerKey}:{slug}` — so a mapping
already names both the broadcaster and the show. Two cases remain:

| Monitored series | Target produced |
|---|---|
| Has a `ShowMapping` for its TVDB id | Exactly one target: the mapped show, on the mapped provider |
| Unmapped | One target per provider **this host serves**, keyed on the series title |

The unmapped fan-out is acceptable because a crawler's `CrawlShowAsync` searches by title and returns
`[]` when the broadcaster has nothing — a miss costs one search. It is also self-correcting: once a
grab produces a mapping, the fan-out collapses to the single mapped target.

## Why targets are filtered to the host's own providers

Each agent runs its own scheduler and its own crawlers, and `PublishAsync` keeps the command in-process
(durable local queues). A `CrawlShowCommand` for `ard` dispatched from the ZDF agent is dropped by
`CrawlShowHandler` with "no crawler registered". So the pre-warm asks the host which providers it
serves — `IEnumerable<IBroadcasterCrawler>` — and emits nothing else.

## Scope

1. **`IArrClient.GetMonitoredAsync`** — Sonarr `GET /api/v3/series` filtered to `monitored: true`;
   Radarr `GET /api/v3/movie` the same way. Returns `(TvdbId?, Title)`; Radarr carries a TMDB id, not a
   TVDB one, so its ids stay null and it matches by title only.
2. **`PreWarmedCrawlTargets`** in `Application/Crawling` — an Action: enabled instances → monitored
   series → mapped/unmapped targets → filtered to this host's providers → capped.
3. **`CrawlSchedulerService` composes per cycle** — configured targets first, pre-warmed ones merged in
   after, deduplicated. Re-read every cycle so newly monitored shows appear without a restart.
4. **Configuration** — `Crawl:PreWarmFromArrInstances` (default **false**) and `Crawl:PreWarmMaxTargets`
   (default 50).
5. **Degrade quietly** — per-instance try/catch; any failure logs and yields the configured list alone.
   A configured target can never be removed or overwritten by this.

## The cap is not decoration

Someone monitoring 200 series on a host serving two providers would otherwise schedule 400 crawls per
cycle, each a broadcaster search plus a detail fetch per hit. That is a load we would be pointing at
ARD and ZDF every six hours. `PreWarmMaxTargets` bounds it, mapped targets are preferred over
fan-out when the cap bites, and the scheduler logs what it dropped rather than silently truncating.

## Not in scope

- Radarr movies as anything other than title queries — the catalog models shows and episodes.
- Improving the *matching* itself. If a title does not find the show, that is `ShowMappings` /
  `TvdbShowResolution` territory, and the operator already has a UI for it.

## Testing

- `GetMonitoredAsync` against stubbed Sonarr and Radarr payloads, including the `monitored: false` filter.
- Target composition: mapped id → single precise target; unmapped → fan-out limited to the host's
  providers; configured targets always present; cap applied with mapped targets surviving first.
- An unreachable instance yields the configured list and logs, rather than throwing or emptying it.
