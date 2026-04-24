using System.Text.Json;
using Mediathek;
using Mediathek.Crawlers;
using Mediathek.Crawlers.Ard;
using Mediathek.Crawlers.Zdf;
using Mediathek.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

public class MainTest(ITestOutputHelper output)
{
    private const string DbFile = "playground.db";

    // ── DI setup ──────────────────────────────────────────────────────────────

    private ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();

        services.AddLogging(b => b
            .SetMinimumLevel(LogLevel.Information)
            .AddProvider(new XunitLoggerProvider(output)));

        services.AddMediathekCrawlers($"Data Source={DbFile}");

        // Remove the hosted scheduler — we drive crawls directly
        var scheduler = services.FirstOrDefault(
            d => d.ImplementationType == typeof(CrawlScheduler));
        if (scheduler is not null)
            services.Remove(scheduler);

        return services.BuildServiceProvider();
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunRecentCrawl()
    {
        await using var provider = BuildServices();

        // Ensure schema exists
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MediathekDbContext>();
            await db.Database.EnsureCreatedAsync();
        }

        // Crawl last 1 day from both ARD and ZDF
        var total = 0;
        var batch = new List<CrawlResult>();
        const int batchSize = 50;

        await using var crawlScope = provider.CreateAsyncScope();
        var ard       = crawlScope.ServiceProvider.GetRequiredService<ArdCrawler>();
        var zdf       = crawlScope.ServiceProvider.GetRequiredService<ZdfCrawler>();
        var persister = crawlScope.ServiceProvider.GetRequiredService<CrawlResultPersister>();

        async Task FlushAsync()
        {
            if (batch.Count == 0) return;
            await persister.PersistBatchAsync(batch);
            batch.Clear();
        }

        output.WriteLine("--- ARD recent crawl (1 day) ---");
        await foreach (var r in ard.CrawlRecentAsync(daysPast: 1))
        {
            batch.Add(r);
            total++;
            if (batch.Count >= batchSize) await FlushAsync();
        }
        output.WriteLine($"ARD: {total} episodes crawled");

        var zdfStart = total;
        output.WriteLine("--- ZDF recent crawl (1 day) ---");
        await foreach (var r in zdf.CrawlRecentAsync(daysPast: 1))
        {
            batch.Add(r);
            total++;
            if (batch.Count >= batchSize) await FlushAsync();
        }
        output.WriteLine($"ZDF: {total - zdfStart} episodes crawled");

        await FlushAsync();

        // Report DB state
        var db2 = crawlScope.ServiceProvider.GetRequiredService<MediathekDbContext>();
        var showCount    = await db2.Shows.CountAsync();
        var episodeCount = await db2.Episodes.CountAsync();
        var streamCount  = await db2.Streams.CountAsync();

        output.WriteLine($"--- DB summary ---");
        output.WriteLine($"Total crawled : {total}");
        output.WriteLine($"Shows         : {showCount}");
        output.WriteLine($"Episodes      : {episodeCount}");
        output.WriteLine($"Streams       : {streamCount}");
    }
}

// ── Trace test: one ARD item, step by step ────────────────────────────────────

public class TraceTest(ITestOutputHelper output)
{
    /// <summary>
    /// Traces the complete HTTP sequence for a single ARD episode.
    /// Run this to understand the protocol, document URLs, and eventually
    /// extract calls to .bru files. Set breakpoints freely — nothing runs in parallel.
    /// </summary>
    [Fact]
    public async Task TraceOneArdItem()
    {
        using var http = new HttpClient(new HttpTraceHandler(output) { BodyPreviewLength = 1200 });
        http.Timeout    = TimeSpan.FromSeconds(30);
        http.DefaultRequestHeaders.Add("User-Agent", "MediathekCrawler/1.0");

        // ── Step 1: Day page → collect item IDs ───────────────────────────────
        var today  = DateTime.Today.ToString("yyyy-MM-dd");
        var dayUrl = string.Format(ArdConstants.DayPageUrl, today, "daserste");

        output.WriteLine($"\n{"════════════════════════════════════════════════════════════"}");
        output.WriteLine($"STEP 1 — Day page ({today}, channel: daserste)");
        output.WriteLine($"{"════════════════════════════════════════════════════════════"}");

        var dayDoc = await FetchJsonAsync(http, dayUrl);
        var itemIds = ArdParser.ParseDayPage(dayDoc);

        output.WriteLine($"\n→ Parsed {itemIds.Count} item ID(s)");
        for (var i = 0; i < Math.Min(5, itemIds.Count); i++)
            output.WriteLine($"  [{i}] {itemIds[i]}");
        if (itemIds.Count > 5)
            output.WriteLine($"  … {itemIds.Count - 5} more");

        Assert.NotEmpty(itemIds);
        var itemId = itemIds[0];

        // ── Step 2: Item detail page ──────────────────────────────────────────
        var itemUrl = string.Format(ArdConstants.ItemUrl, itemId);

        output.WriteLine($"\n{"════════════════════════════════════════════════════════════"}");
        output.WriteLine($"STEP 2 — Item detail (id: {itemId})");
        output.WriteLine($"{"════════════════════════════════════════════════════════════"}");

        var itemDoc = await FetchJsonAsync(http, itemUrl);
        var parsed  = ArdParser.ParseItemPage(itemDoc);

        // ── Step 3: Parsed result summary ─────────────────────────────────────
        output.WriteLine($"\n{"════════════════════════════════════════════════════════════"}");
        output.WriteLine($"STEP 3 — Parsed result");
        output.WriteLine($"{"════════════════════════════════════════════════════════════"}");

        if (parsed is null)
        {
            output.WriteLine("(parser returned null — check body above for clues)");
            Assert.Fail("ParseItemPage returned null");
            return;
        }

        output.WriteLine($"  Show     : {parsed.Topic}");
        output.WriteLine($"  Title    : {parsed.Title}");
        output.WriteLine($"  Channel  : {parsed.BroadcasterKey}");
        output.WriteLine($"  Date     : {parsed.BroadcastTime:u}");
        output.WriteLine($"  Duration : {parsed.Duration}");
        output.WriteLine($"  GeoBlock : {parsed.GeoBlocked}");
        output.WriteLine($"  Streams  : {parsed.Streams.Count}");
        foreach (var s in parsed.Streams)
            output.WriteLine($"    [{s.Quality,-6}] {s.Url}");
        output.WriteLine($"  AD       : {parsed.StreamsAd.Count}");
        output.WriteLine($"  DGS      : {parsed.StreamsDgs.Count}");
        output.WriteLine($"  OV       : {parsed.StreamsOv.Count}");
        output.WriteLine($"  Subtitles: {parsed.Subtitles.Count}");
        foreach (var s in parsed.Subtitles)
            output.WriteLine($"    {s.Url}");
        output.WriteLine($"  Related  : {parsed.RelatedItemIds.Count}");
        foreach (var id in parsed.RelatedItemIds)
            output.WriteLine($"    {id}");
    }

    private static async Task<JsonElement> FetchJsonAsync(HttpClient http, string url)
    {
        var resp = await http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync();
        var doc = await JsonDocument.ParseAsync(stream);
        return doc.RootElement.Clone();
    }
}

// ── xUnit logger ──────────────────────────────────────────────────────────────

sealed class XunitLoggerProvider(ITestOutputHelper output) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new XunitLogger(output, categoryName);
    public void Dispose() { }
}

sealed class XunitLogger(ITestOutputHelper output, string category) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel level) => level >= LogLevel.Information;
    public void Log<TState>(LogLevel level, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(level)) return;
        try { output.WriteLine($"[{level,-11}] {category}: {formatter(state, exception)}"); }
        catch { /* test runner may have already closed the output writer */ }
    }
}
