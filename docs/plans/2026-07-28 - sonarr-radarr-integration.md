# 2026-07-28 — Making the Sonarr/Radarr integration actually work (#6 + matching)

**Status:** planned · **Milestone:** Foundation / ARD and KiKA indexer
**Builds on:** #4 (instances + outbound client), [DR-011](../architecture/DR-011-search-driven-indexing.md)

"Get the Sonarr/Radarr integration done" is more than #6. #6 gives us a crawl work-list; it does **not**
make Sonarr able to *match* what we return. Reviewing the current surface, matching is the part that would
leave the integration looking broken even with #6 finished.

## What is actually missing

### 1. We never tell Sonarr which series a release belongs to

`NewznabXml.Feed` emits only two attributes per item:

```xml
<newznab:attr name="category" value="5000" />
<newznab:attr name="size" value="..." />
```

No `tvdbid`, no `season`, no `episode`. So Sonarr has to identify the series **purely by parsing our release
title**, against a Mediathek catalog whose titles are famously inconsistent — the problem both MediathekArr
and RundfunkArr spend most of their effort on. A `tvdbid` attribute short-circuits all of it: Sonarr trusts
the id over the string.

`Show.TvdbId` already exists on the entity and is **never set by anything**.

### 2. We do not accept `tvdbid` as a search parameter

The endpoint binds `q`, `season`, `ep` only, and `caps` advertises `supportedParams="q,season,ep"`. Sonarr
reads caps and adapts, so today it falls back to title searches — which works, but throws away the reliable
path. Advertising and honouring `tvdbid` lets Sonarr ask us the unambiguous question.

### 3. Nothing links an `*arr` series to a broadcaster show

`CrawlTarget(ProviderKey, ShowQuery)` is a broadcaster + a title substring. A Sonarr series is a title, a
`tvdbId`, a year, and a set of `alternateTitles`. Bridging the two **is** the integration, and it is the hard
part — not the HTTP call.

## Research: why the names differ, and whether there is a trick (2026-07-28)

**Why they differ at all.** TVDB is community-maintained and its *primary* title is often the English or
canonical form — but TVDB v4 stores per-language **translations and aliases**, and its **search endpoint
indexes them**. The Mediathek uses the broadcaster's own on-air branding. So the mismatch is real, yet TVDB
usually *does* hold the German name; it just is not the primary title. That reframes the problem: do not
compare primary titles, query TVDB's search with the German title and let it match its own aliases.

**The trick that exists: Wikidata.** `P4835` is "TheTVDB series ID", and German shows carry it. A keyless
SPARQL query maps a German title (label *or* alias) to a TVDB id, with the broadcaster attached via `P449`
for disambiguation. Measured live:

| Broadcaster | Series with a TVDB id | Coverage |
|---|---|---|
| ZDF | 359 / 2467 | **14.6%** |
| Das Erste | 270 / 1770 | **15.3%** |
| KiKA | 49 / 101 | **48.5%** |
| ZDFneo | 42 / 90 | **46.7%** |
| **Total** | **720 / 4428** | **16.3%** |

So it is a genuine trick but a *partial* one — free, no API key, no scraping, and notably good for KiKA
(cartoons are well covered, which is exactly the geo-restricted content we already handle). It cannot be the
whole answer at 16%.

Two concrete gotchas the data surfaced:

- **Disambiguation is required, and Wikidata provides it.** *Die Biene Maja* returns **two** TVDB ids —
  73518 (1975, TV Asahi) and 266275 (2013, ZDF). Without the broadcaster we would pick wrong half the time.
- **ARD is a federation, so `P449` is often the regional member, not "Das Erste".** *extra 3* resolves to
  tvdb 255986 with network **Norddeutscher Rundfunk**. Filtering on "Das Erste" alone would miss it. Accept
  the ARD member set (NDR, WDR, BR, SWR, MDR, HR, RBB, SR).
- Case matters: `extra 3` missed, `Extra 3` matched — normalise before querying.
- Genuinely ambiguous titles (*Panorama*) match nothing useful and belong in the manual tail.

**Conclusion: we do not have to mimic MediathekArr/RundfunkArr wholesale, but we cannot avoid a manual tail
either.** The layered design below reduces the manual work rather than eliminating it — which is the honest
version of "is there a neat trick".

### Layered matching, cheapest first

1. **TVDB search by the German Mediathek title** — primary. Their search indexes translations and aliases,
   which is precisely the "names differ" problem. Uses **`TvdbClient`** (below).
2. **Wikidata corroboration** — keyless, and the way to disambiguate multiple TVDB candidates by broadcaster.
   Also usable to pre-seed a mapping table in bulk, offline.
3. **Manual override in the UI** — for the tail. RundfunkArr's curated `shows.json` is the evidence that this
   step is unavoidable; the goal is only to make it small.

### TVDB client

Use **`TvdbClient`** (`Chrison-dev/TvdbApi`, NuGet `TvdbClient` 4.7.12) rather than hand-rolling — it is
maintained in-house. Packages: `TvdbClient`, `TvdbClient.Models` (`Tvdb.Models`), `TvdbClient.Abstractions`.

```csharp
builder.Configuration.AddTvdbClient();
builder.Services.AddTvdbClient(builder.Configuration);

var series = sp.GetRequiredService<ISeriesClient>();
var record = (await series.SeriesGetAsync(121361)).Data;
```

Configured via `TvdbConfiguration:BaseUrl / ApiKey / Pin`; login and auth headers are handled for us.
**Needs an API key** (subscriber PIN optional) — the one external dependency this work adds, and the reason
step 2 matters: Wikidata keeps basic mapping working for operators without a TVDB key.

## Shape (three reviewable PRs)

### PR 1 — Emit and accept TVDB ids · `feat/arr-tvdb-matching`

Independent of #6 and the highest value per line changed.

- `Release` gains `TvdbId`; `NewznabXml` emits `newznab:attr name="tvdbid"`, plus `season` and `episode`
  where known.
- The endpoint binds `tvdbid` (and `rid`/`imdbid` are explicitly ignored for now); `caps` advertises
  `supportedParams="q,tvdbid,season,ep"`.
- `SearchReleasesQuery` gains `TvdbId`; a search by id resolves to the mapped show and filters on it,
  falling back to `q` when the id is unknown to us.
- `Show.TvdbId` is finally populated — by PR 2, until then it stays null and behaviour is unchanged.

### PR 2 — Pull monitored series (#6) · `feat/arr-monitored-series` — **DEFERRED**

On the back burner by decision 2026-07-28. Not needed for matching to work, and per DR-011 nothing may
depend on a configured instance anyway.


- `IArrClient` gains `GetMonitoredSeriesAsync` — Sonarr `GET /api/v3/series` filtered to `monitored: true`;
  Radarr `GET /api/v3/movie`. Returns title, `tvdbId`/`tmdbId`, year, alternate titles.
- A background service polls each **enabled** instance on an interval and merges results into the standing
  crawl list, **never replacing** it: a hand-configured `Crawl:Targets` entry must not vanish because an
  instance went offline mid-poll.
- Failures degrade quietly per instance — log, keep going, never empty the list.
- Opt-in (`Crawl:PreWarmFromArrInstances`, default false) per DR-011: nothing may *depend* on an instance
  being configured.

### PR 3 — Show mapping · `feat/arr-show-mapping`

The bridge, and where the effort really goes.

- A `ShowMapping` record: `TvdbId` ↔ (`ProviderKey`, broadcaster show id/title), with provenance
  (auto-matched vs. operator-confirmed).
- Auto-match attempt: normalise both sides (lower-case, strip punctuation and years, fold umlauts —
  `ä→ae` etc., since Mediathek titles and TVDB disagree constantly), then compare title and alternate
  titles.
- **Auto-matching will not be enough.** RundfunkArr resolves this with human-curated `shows.json` +
  per-show `rulesets.json`, and that is a fair signal. So: surface unmatched monitored series in the UI and
  let the operator pick the broadcaster show manually. Manual mappings win over auto ones.
- Once mapped, crawled episodes inherit `Show.TvdbId`, which is what makes PR 1's attribute meaningful.

## Decisions

**PR 1 goes first, and is useful alone.** It improves matching for the shows we already crawl without
needing any `*arr` instance configured — consistent with DR-011's rule that nothing may depend on one.

**Radarr is deliberately second-class for now.** It uses `tmdbId`, not `tvdbId`, and the movie catalog on
ARD/ZDF is thin (MediathekArr's README says the same: *"You can find a few movies via interactive search,
but not a lot"*). Model the field, wire the fetch, do not over-invest in movie matching.

**Do not scope-creep into a fuzzy-matching engine.** Exact-after-normalisation plus a manual override
covers the realistic case. Anything cleverer earns its place only once we can see real failures against a
real library.

## Testing

- Unit: TVDB attribute emission; `tvdbid` search resolving and falling back; title normalisation
  (umlauts, punctuation, years); merge-not-replace semantics for the crawl list; an offline instance not
  emptying the list.
- `ArrHttpClient` against a stubbed handler for the series/movie endpoints, including a monitored/unmonitored
  mix and pagination if Sonarr paginates.
- **Live, against a real instance** (Christian has Sonarr and Radarr): `[Trait("Category","Live")]` reading
  `KRAUTWATCH_TEST_SONARR_URL` / `KRAUTWATCH_TEST_SONARR_APIKEY`, skipping when unset — the same convention
  `KRAUTWATCH_TEST_PROXY` already uses for the geo-proxy live test. Credentials stay in the operator's
  environment and never enter the repo.
- End-to-end worth doing by hand once: point real Sonarr at Krautwatch as a Newznab indexer, trigger an
  interactive search for a monitored German public-TV show, and confirm Sonarr both finds and *matches* the
  release.
