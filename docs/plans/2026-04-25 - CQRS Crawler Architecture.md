# CQRS Crawler Architecture

**Date:** 2026-04-25
**Status:** Approved — implementation in progress

## Context

The playground (`tests/Playground/`) has working crawlers for ARD and ZDF — direct C# translations from Java originals. They are monolithic: HTTP fetching, JSON parsing, language-variant splitting, and related-item recursion all happen inside a single method per crawler. No step is independently testable without real HTTP calls.

The goal is to build a production implementation, side-by-side, using CQRS — every step in the pipeline is an independently testable handler. ARD and ZDF are separate projects that know nothing about each other; they both implement the same crawler contract from a shared Core project.

---

## How the Crawlers Work (Dissected)

### ARD

```
Full:   [A-Z topics per client] → [compilation URLs] → [item IDs] → [episode detail JSON] → [CrawlResult]
Recent: [day EPG per client×day]                    → [item IDs] → [episode detail JSON] → [CrawlResult]
```

One item ID can produce **up to 4 CrawlResults** (German / Audio-Description / Sign Language / Original Version) plus recursion into `relatedItemIds`.

### ZDF

```
Full:   [A-Z letter pages, paged] → [show canonical refs]           ┐
        [special collections]     → [episode canonical refs]        ├→ [downloads table] → [CrawlResult]
        [season pages, paged]     → [episode canonical refs]        ┘
Recent: [day search per date]     → [canonical refs → season 0 fetch]
```

---

## Project Structure

Three new projects alongside the existing `src/` layout:

```
src/
├── MediathekNext.Crawlers.Core/      ← interfaces, shared types, core handlers
├── MediathekNext.Crawlers.Ard/       ← ARD implementation (depends on Core only)
└── MediathekNext.Crawlers.Zdf/       ← ZDF implementation (depends on Core only)
```

ARD and ZDF are completely unaware of each other. Both implement `ICrawler` from Core.

### MediathekNext.Crawlers.Core/

```
ICrawler.cs                   ← CrawlFullAsync / CrawlRecentAsync contract
CrawlResult.cs                ← sealed record CrawlResult + StreamEntry + SubtitleEntry + enums
IRawResponseStore.cs          ← interface: store raw JSON as it's fetched
ICrawlRepository.cs           ← interface: upsert one episode to DB immediately after parse
PersistCrawlResult.cs         ← handler: PersistCrawlResultCommand → ICrawlRepository
StoreRawResponse.cs           ← handler: StoreRawResponseCommand(source, itemId, json) → IRawResponseStore
JsonNav.cs                    ← null-safe JSON helpers (shared by both implementations)
CrawlSummary.cs               ← CrawlSummary result record
```

### MediathekNext.Crawlers.Ard/

```
ArdModels.cs                  ← IArdClient interface + ArdEpisodeRaw + ArdStreamRaw
ArdClient.cs                  ← IArdClient impl: HttpClient + System.Text.Json → ArdEpisodeRaw
ArdConstants.cs               ← TopicsUrl, DayPageUrl, ItemUrl, TopicClients, DayClients
ParseArdEpisode.cs            ← pure handler: ArdEpisodeRaw → IReadOnlyList<CrawlResult>
CrawlArdFull.cs               ← orchestration command + handler (full crawl)
CrawlArdRecent.cs             ← orchestration command + handler (recent crawl)
ArdCrawler.cs                 ← ICrawler impl: delegates to CrawlArdFull/Recent handlers
ArdCrawlerServiceExtensions.cs ← AddArdCrawler() DI method
```

### MediathekNext.Crawlers.Zdf/

```
ZdfModels.cs                  ← IZdfClient interface + ZdfEpisodeRaw + ZdfEpisodeRef + ZdfLetterPageResult
ZdfClient.cs                  ← IZdfClient impl: GraphQL fetches + downloads table parsing
ZdfConstants.cs               ← AuthKey, GraphQL hashes, LetterPageCount, SpecialCollections
ParseZdfEpisode.cs            ← pure handler: ZdfEpisodeRaw → CrawlResult?
CrawlZdfFull.cs               ← orchestration command + handler (full crawl)
CrawlZdfRecent.cs             ← orchestration command + handler (recent crawl)
ZdfCrawler.cs                 ← ICrawler impl
ZdfCrawlerServiceExtensions.cs
```

### Infrastructure additions (DB implementations of Core interfaces)

```
src/MediathekNext.Infrastructure/Crawling/
├── CrawlRepository.cs              ← implements ICrawlRepository (EF Core upsert)
├── RawResponseStore.cs             ← implements IRawResponseStore (DB table or file store)
└── CrawlingServiceExtensions.cs   ← register implementations
```

---

## Core Interfaces

```csharp
// Core/ICrawler.cs
public interface ICrawler
{
    string Source { get; }   // "ard", "zdf"
    Task<CrawlSummary> CrawlFullAsync(CancellationToken ct = default);
    Task<CrawlSummary> CrawlRecentAsync(int daysPast = 7, CancellationToken ct = default);
}

// Core/IRawResponseStore.cs
public interface IRawResponseStore
{
    Task StoreAsync(string source, string itemId, string json, CancellationToken ct = default);
}

// Core/ICrawlRepository.cs
public interface ICrawlRepository
{
    Task UpsertAsync(CrawlResult result, CancellationToken ct = default);
}

// Ard/ArdModels.cs
public interface IArdClient
{
    Task<IReadOnlyList<string>> FetchTopicUrlsAsync(string clientKey, CancellationToken ct = default);
    Task<IReadOnlyList<string>> FetchTopicItemIdsAsync(string topicUrl, CancellationToken ct = default);
    Task<IReadOnlyList<string>> FetchDayItemIdsAsync(string clientKey, DateOnly date, CancellationToken ct = default);
    Task<(string RawJson, ArdEpisodeRaw Episode)?> FetchEpisodeAsync(string itemId, CancellationToken ct = default);
}
```

`FetchEpisodeAsync` returns both the raw JSON **and** the typed model so the orchestrator can immediately store both.

---

## Immediate Persistence: The Two Side-Effect Steps

Every fetch + parse fires a side-effect **immediately** (not batched):

```
FetchEpisode(itemId)
  └─► StoreRawResponse(source="ard", itemId, rawJson)    ← stored to DB/disk right away
  └─► ParseArdEpisode(episodeRaw)
        └─► PersistCrawlResult(crawlResult)              ← persisted to DB right away (per episode)
```

After any partial crawl (crash, cancellation) the DB has everything found so far. Raw JSON storage enables replay: re-run the parse step over stored JSON without re-fetching.

---

## Parse Handler (Pure, No I/O)

```csharp
// Ard/ParseArdEpisode.cs
public record ParseArdEpisodeCommand(ArdEpisodeRaw Episode);

public sealed class ParseArdEpisodeHandler
{
    // No constructor deps — pure mapping, fully testable with plain objects
    public IReadOnlyList<CrawlResult> Handle(ParseArdEpisodeCommand cmd)
    {
        // Produces up to 4 CrawlResults (German, AD, DGS, OV)
        // Language-variant title suffixes (" (Audiodeskription)" etc.) applied here
        // RelatedItemIds are NOT expanded here — orchestrator handles recursion
    }
}
```

---

## Orchestration Handler (ARD Full)

```csharp
// Ard/CrawlArdFull.cs
public record CrawlArdFullCommand;

public sealed class CrawlArdFullHandler(
    IArdClient client,
    ParseArdEpisodeHandler parser,
    StoreRawResponseHandler rawStore,
    PersistCrawlResultHandler persister,
    ILogger<CrawlArdFullHandler> log)
{
    private const int Parallelism = 10;

    public async Task<CrawlSummary> HandleAsync(CrawlArdFullCommand _, CancellationToken ct = default)
    {
        // Step 1: Fetch topic URLs for all clients (parallel)
        // Step 2: Fetch item IDs from each topic URL (parallel, bounded)
        // Step 3: For each item ID (parallel, bounded):
        //   a. FetchEpisode → (rawJson, ArdEpisodeRaw)
        //   b. StoreRawResponse immediately
        //   c. ParseArdEpisode → CrawlResult[]
        //   d. PersistCrawlResult immediately for each result
        //   e. Recurse into relatedItemIds
    }
}
```

---

## Testability Matrix

| Handler | Mocked | How |
|---------|--------|-----|
| `ParseArdEpisodeHandler` | Nothing | Construct `ArdEpisodeRaw` in C#, assert output |
| `ParseZdfEpisodeHandler` | Nothing | Same |
| `CrawlArdFullHandler` | `IArdClient`, `IRawResponseStore`, `ICrawlRepository` | NSubstitute |
| `PersistCrawlResultHandler` | `ICrawlRepository` | NSubstitute |
| `StoreRawResponseHandler` | `IRawResponseStore` | NSubstitute |
| `ArdClient` / `ZdfClient` | Nothing | Integration tests (existing playground) |

---

## Key Differences from Java/Playground Approach

| Aspect | Playground | New CQRS |
|--------|------------|----------|
| Structure | One fat class per broadcaster | Discrete handlers per step, separate projects |
| Projects | Single playground project | Core + per-broadcaster project |
| Parsing | Inlined in fetch | Dedicated pure handler |
| JSON coupling | `JsonElement` passed around | Typed raw records + raw JSON string at boundary |
| Language variants | Inside `FetchItemAsync` | Inside `ParseArdEpisodeHandler` |
| Persistence | End-of-crawl batch | Immediate per-episode as each is found |
| Raw JSON | Discarded | Stored to `IRawResponseStore` on every fetch |
| Testability | Integration only | Each step unit-testable in isolation |
