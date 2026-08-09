using Krautwatch.Domain.Interfaces;
using Krautwatch.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Krautwatch.Application.Downloads;

/// <summary>
/// Refreshes the cached public proxy list on a schedule (Mode B, #45) — mirrors
/// <c>CrawlSchedulerService</c>. Opens a fresh DI scope per run so the scoped repositories aren't
/// captured by this singleton hosted service. Hosted by the Downloader agent (the host that actually
/// needs egress).
/// </summary>
/// <remarks>
/// Mode B is now switchable from the UI (#54), so this re-checks each pass rather than returning at
/// startup. Bailing once meant enabling it in the UI did nothing until the container was restarted —
/// and the service is registered unconditionally for the same reason.
/// </remarks>
public class ProxyRefreshService(
    IServiceScopeFactory scopes, ProxyListOptions options, ILogger<ProxyRefreshService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!await DelayAsync(options.InitialDelay, stoppingToken)) return;

        var lastEnabled = (bool?)null;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();

                var enabled = await IsEnabledAsync(scope.ServiceProvider, stoppingToken);
                if (enabled != lastEnabled)
                {
                    logger.LogInformation(
                        "Proxy-list refresh is {State}.", enabled ? "enabled" : "disabled — idle");
                    lastEnabled = enabled;
                }

                if (enabled)
                {
                    var handler = scope.ServiceProvider.GetRequiredService<RefreshProxyListHandler>();
                    await handler.HandleAsync(stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Proxy-list refresh failed.");
            }

            if (!await DelayAsync(options.RefreshInterval, stoppingToken)) return;
        }
    }

    /// <summary>
    /// Mode B is on when either configuration or the stored setting says so. Either alone is enough: an
    /// operator who enabled it in compose must not have it silently switched off by a stale row.
    /// </summary>
    private async Task<bool> IsEnabledAsync(IServiceProvider services, CancellationToken ct)
    {
        if (options.Enabled) return true;

        try
        {
            var settings = services.GetService<ISettingsRepository>();
            return settings is not null && (await settings.GetAsync(ct)).EgressProxyListEnabled;
        }
        catch (Exception ex)
        {
            // Unreadable settings must not flip Mode B on by accident; fall back to configuration.
            logger.LogWarning(ex, "Could not read the stored proxy-list setting; treating it as off.");
            return false;
        }
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); return true; }
        catch (OperationCanceledException) { return false; }
    }
}
