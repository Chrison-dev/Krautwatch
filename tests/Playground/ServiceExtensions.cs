using Mediathek.Crawlers;
using Mediathek.Crawlers.Ard;
using Mediathek.Crawlers.Zdf;
using Mediathek.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mediathek;

// ── DI registration extension ─────────────────────────────────────────────────

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMediathekCrawlers(
        this IServiceCollection services,
        string connectionString)
    {
        services.AddDbContext<MediathekDbContext>(o =>
            o.UseSqlite(connectionString));   // swap for UseSqlServer / UseNpgsql as needed

        services.AddScoped<CrawlResultPersister>();

        // Named HttpClients — ARD needs no special headers, ZDF auth is added per-request
        services.AddHttpClient<ArdCrawler>(c =>
        {
            c.Timeout = TimeSpan.FromSeconds(30);
            c.DefaultRequestHeaders.Add("User-Agent", "MediathekCrawler/1.0");
        });

        services.AddHttpClient<ZdfCrawler>(c =>
        {
            c.Timeout = TimeSpan.FromSeconds(30);
            c.DefaultRequestHeaders.Add("User-Agent", "MediathekCrawler/1.0");
        });

        services.AddHostedService<CrawlScheduler>();

        return services;
    }
}

// ── Hosted service: scheduled crawl ──────────────────────────────────────────

public class CrawlScheduler(
    IServiceScopeFactory scopeFactory,
    ILogger<CrawlScheduler> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Run full crawl on startup, then every 4 hours
        while (!ct.IsCancellationRequested)
        {
            await RunCrawlAsync(fullMode: true, ct);
            await Task.Delay(TimeSpan.FromHours(4), ct);
        }
    }

    private async Task RunCrawlAsync(bool fullMode, CancellationToken ct)
    {
        log.LogInformation("Starting {Mode} crawl", fullMode ? "full" : "recent");

        await using var scope     = scopeFactory.CreateAsyncScope();
        var ardCrawler  = scope.ServiceProvider.GetRequiredService<ArdCrawler>();
        var zdfCrawler  = scope.ServiceProvider.GetRequiredService<ZdfCrawler>();
        var persister   = scope.ServiceProvider.GetRequiredService<CrawlResultPersister>();

        var batch = new List<CrawlResult>();
        const int batchSize = 200;

        async Task FlushAsync()
        {
            if (batch.Count == 0) return;
            await persister.PersistBatchAsync(batch, ct);
            batch.Clear();
        }

        // ARD
        var ardStream = fullMode
            ? ardCrawler.CrawlFullAsync(ct)
            : ardCrawler.CrawlRecentAsync(ct: ct);

        await foreach (var r in ardStream)
        {
            batch.Add(r);
            if (batch.Count >= batchSize) await FlushAsync();
        }

        // ZDF
        var zdfStream = fullMode
            ? zdfCrawler.CrawlFullAsync(ct)
            : zdfCrawler.CrawlRecentAsync(ct: ct);

        await foreach (var r in zdfStream)
        {
            batch.Add(r);
            if (batch.Count >= batchSize) await FlushAsync();
        }

        await FlushAsync();
        log.LogInformation("Crawl complete");
    }
}
