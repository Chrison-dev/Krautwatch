using MediathekNext.Domain.Interfaces;
using MediathekNext.Infrastructure.System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TickerQ.Utilities.Base;

namespace MediathekNext.Infrastructure.Jobs;

/// <summary>
/// Cron-based catalog refresh — runs every N hours (default 6).
/// Replaces the hand-rolled Task.Delay loop in CatalogRefreshService.
///
/// TickerQ handles:
///   - Schedule persistence (survives restarts)
///   - Misfire recovery (runs immediately if missed while app was down)
///   - Dashboard visibility + manual trigger
///   - Retry on failure (3 attempts, 60s/120s/300s backoff)
///
/// The cron expression is seeded in TickerQSeedService.
/// To change the schedule, update it there or edit it via the dashboard.
/// </summary>
public class CatalogRefreshJob(
    IServiceScopeFactory scopeFactory,
    SystemStatusService status,
    ILogger<CatalogRefreshJob> logger)
{
    public const string FunctionName = "CatalogRefresh";

    [TickerFunction(FunctionName)]
    public async Task RunAsync(TickerFunctionContext context, CancellationToken ct)
    {
        logger.LogInformation("CatalogRefreshJob started");
        var isInitialRun = status.GetSnapshot().State != Infrastructure.System.AppState.Ready;

        if (isInitialRun)
            status.MarkCatalogStarting();
        else
            status.MarkCatalogRefreshing();

        var started = DateTimeOffset.UtcNow;

        try
        {
            using var scope = scopeFactory.CreateScope();
            var provider    = scope.ServiceProvider.GetRequiredService<ICatalogProvider>();
            var repository  = scope.ServiceProvider.GetRequiredService<IEpisodeRepository>();

            var progress = new Progress<CatalogFetchProgress>(p =>
            {
                switch (p.Phase)
                {
                    case CatalogFetchPhase.Downloading:
                        status.MarkCatalogDownloading(p.PercentComplete);
                        break;
                    case CatalogFetchPhase.Parsing:
                        status.MarkCatalogParsing(p.EntriesParsed, p.TotalEntries);
                        break;
                }
            });

            var episodes = await provider.FetchCatalogAsync(progress, ct);

            if (episodes.Count == 0)
            {
                logger.LogWarning("Catalog returned 0 episodes — skipping upsert");
                // Don't throw — TickerQ would retry; zero results is usually a provider issue
                return;
            }

            await repository.UpsertManyAsync(episodes, ct);

            var elapsed = DateTimeOffset.UtcNow - started;
            logger.LogInformation(
                "Catalog refresh done: {Count:N0} episodes in {Elapsed:mm\\:ss}",
                episodes.Count, elapsed);

            status.MarkCatalogReady(episodes.Count, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("CatalogRefreshJob cancelled");
            throw; // Let TickerQ know the job was interrupted
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "CatalogRefreshJob failed after {Elapsed:mm\\:ss}",
                DateTimeOffset.UtcNow - started);
            status.MarkError("Catalog refresh", ex.Message);
            throw; // Rethrow so TickerQ retries
        }
    }
}
