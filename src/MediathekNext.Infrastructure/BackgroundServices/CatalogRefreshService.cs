using MediathekNext.Domain.Interfaces;
using MediathekNext.Infrastructure.System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MediathekNext.Infrastructure.BackgroundServices;

/// <summary>
/// Periodically fetches the catalog from ICatalogProvider and upserts episodes.
/// Reports progress to SystemStatusService so the init screen can display it.
/// Runs only in core and standalone roles (single instance — DR-002).
/// </summary>
public class CatalogRefreshService(
    IServiceScopeFactory scopeFactory,
    SystemStatusService status,
    ILogger<CatalogRefreshService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("CatalogRefreshService started");

        // Mark DB step complete — we're running, so migrations already succeeded
        status.MarkDatabaseReady();

        await RefreshAsync(isInitial: true, ct: stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = await GetRefreshIntervalAsync(stoppingToken);
            logger.LogInformation("Next catalog refresh in {Hours}h", interval.TotalHours);
            await Task.Delay(interval, stoppingToken);
            await RefreshAsync(isInitial: false, ct: stoppingToken);
        }
    }

    private async Task RefreshAsync(bool isInitial, CancellationToken ct)
    {
        logger.LogInformation("Starting catalog refresh (initial={IsInitial})", isInitial);
        var started = DateTimeOffset.UtcNow;

        if (isInitial)
            status.MarkCatalogStarting();
        else
            status.MarkCatalogRefreshing();

        try
        {
            using var scope = scopeFactory.CreateScope();
            var provider   = scope.ServiceProvider.GetRequiredService<ICatalogProvider>();
            var repository = scope.ServiceProvider.GetRequiredService<IEpisodeRepository>();

            // Progress callback wired into the catalog provider
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
                logger.LogWarning("Catalog refresh returned 0 episodes — skipping upsert");
                status.MarkError("Catalog refresh", "Provider returned 0 episodes");
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
            logger.LogInformation("Catalog refresh cancelled");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Catalog refresh failed");
            status.MarkError("Catalog refresh", ex.Message);
            status.ClearError(); // allow retry on next interval
        }
    }

    private async Task<TimeSpan> GetRefreshIntervalAsync(CancellationToken ct)
    {
        try
        {
            using var scope   = scopeFactory.CreateScope();
            var settings      = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();
            var appSettings   = await settings.GetAsync(ct);
            return TimeSpan.FromHours(appSettings.CatalogRefreshIntervalHours);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read refresh interval — using 6h default");
            return TimeSpan.FromHours(6);
        }
    }
}
