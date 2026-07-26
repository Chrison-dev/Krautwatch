using Krautwatch.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Krautwatch.Application.Downloads;

/// <summary>
/// Refreshes the cached public proxy list on a schedule (Mode B, #45) — mirrors
/// <c>CrawlSchedulerService</c>. Idle unless <see cref="ProxyListOptions.Enabled"/>. Opens a fresh DI
/// scope per run so the scoped repositories aren't captured by this singleton hosted service. Hosted
/// by the Downloader agent (the host that actually needs egress).
/// </summary>
public class ProxyRefreshService(
    IServiceScopeFactory scopes, ProxyListOptions options, ILogger<ProxyRefreshService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("Proxy-list refresh disabled (Download:ProxyList:Enabled=false) — idle.");
            return;
        }

        if (!await DelayAsync(options.InitialDelay, stoppingToken)) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var handler = scope.ServiceProvider.GetRequiredService<RefreshProxyListHandler>();
                await handler.HandleAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Proxy-list refresh failed.");
            }

            if (!await DelayAsync(options.RefreshInterval, stoppingToken)) return;
        }
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); return true; }
        catch (OperationCanceledException) { return false; }
    }
}
