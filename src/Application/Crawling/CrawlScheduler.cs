using Krautwatch.Domain.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Krautwatch.Application.Crawling;

/// <summary>One scheduled crawl target: a show query on a broadcaster scope.</summary>
public record CrawlTarget(string ProviderKey, string ShowQuery);

/// <summary>
/// Crawl schedule for an agent — bound from the host's <c>Crawl</c> config section. The seed list
/// starts with the shows proven live in PR #34; a Sonarr-driven watchlist (DR-010) supersedes it
/// once the Newznab surface exists.
/// </summary>
public class CrawlOptions
{
    public const string SectionName = "Crawl";

    /// <summary>How often to re-crawl every target.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>Grace period after startup before the first crawl (lets Postgres/Wolverine settle).</summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(10);

    public List<CrawlTarget> Targets { get; set; } = [];

    /// <summary>
    /// Also crawl what the configured Sonarr/Radarr instances monitor (#6). Opt-in, and additive —
    /// <see cref="Targets"/> is always honoured and never replaced by what an instance reports.
    /// </summary>
    public bool PreWarmFromArrInstances { get; set; }

    /// <summary>
    /// Upper bound on pre-warmed targets per cycle.
    /// </summary>
    /// <remarks>
    /// Not decoration: someone monitoring 200 series on a host serving two broadcasters would otherwise
    /// point 400 searches at ARD and ZDF every interval. Mapped shows are kept ahead of title guesses
    /// when it bites, and the count that was dropped is logged.
    /// </remarks>
    public int PreWarmMaxTargets { get; set; } = 50;
}

/// <summary>
/// Emits a <see cref="CrawlShowCommand"/> per configured target on startup and then every
/// <see cref="CrawlOptions.Interval"/>. Dispatch goes through the <see cref="IMessageDispatcher"/>
/// port, so the scheduler carries no transport dependency (DR-009 §5). Hosted by each agent.
/// </summary>
public class CrawlSchedulerService(
    IMessageDispatcher dispatcher,
    CrawlOptions options,
    ILogger<CrawlSchedulerService> logger,
    IServiceScopeFactory? scopes = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Targets.Count == 0 && !options.PreWarmFromArrInstances)
        {
            logger.LogInformation("Crawl scheduler started with no configured targets — idle.");
            return;
        }

        if (!await DelayAsync(options.InitialDelay, stoppingToken)) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            // Recomposed every cycle rather than at startup, so a show newly monitored in Sonarr is
            // picked up on the next pass instead of at the next restart.
            foreach (var target in await TargetsForThisCycleAsync(stoppingToken))
            {
                try
                {
                    await dispatcher.PublishAsync(
                        new CrawlShowCommand(target.ProviderKey, target.ShowQuery), stoppingToken);
                    logger.LogInformation("Scheduled crawl '{Show}' on {Provider}.",
                        target.ShowQuery, target.ProviderKey);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Failed to schedule crawl '{Show}' on {Provider}.",
                        target.ShowQuery, target.ProviderKey);
                }
            }

            if (!await DelayAsync(options.Interval, stoppingToken)) return;
        }
    }

    /// <summary>
    /// The configured targets, plus whatever the <c>*arr</c> instances are monitoring when pre-warm is
    /// on.
    /// </summary>
    /// <remarks>
    /// Configured targets come first and are never displaced: an instance going offline mid-poll must
    /// not silently shrink the standing list. A pre-warm failure of any kind leaves exactly the
    /// behaviour of a deployment that never enabled it.
    /// </remarks>
    private async Task<IReadOnlyList<CrawlTarget>> TargetsForThisCycleAsync(CancellationToken ct)
    {
        if (!options.PreWarmFromArrInstances || scopes is null)
            return options.Targets;

        try
        {
            // A scope per cycle: the repositories and the *arr client are scoped, and this is a
            // singleton hosted service.
            await using var scope = scopes.CreateAsyncScope();

            var preWarm = scope.ServiceProvider.GetRequiredService<PreWarmCrawlTargetsHandler>();

            // The host's own crawlers are the authority on which providers it can serve — a target for
            // any other is dropped by the handler on arrival.
            var providerKeys = scope.ServiceProvider
                .GetServices<IBroadcasterCrawler>()
                .Select(c => c.ProviderKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var preWarmed = await preWarm.HandleAsync(providerKeys, options.PreWarmMaxTargets, ct);

            return options.Targets
                .Concat(preWarmed)
                .Distinct()
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Pre-warming from *arr instances failed — crawling the configured " +
                "targets only this cycle.");

            return options.Targets;
        }
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); return true; }
        catch (OperationCanceledException) { return false; }
    }
}
