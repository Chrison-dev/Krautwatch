using Krautwatch.Domain.Interfaces;
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
}

/// <summary>
/// Emits a <see cref="CrawlShowCommand"/> per configured target on startup and then every
/// <see cref="CrawlOptions.Interval"/>. Dispatch goes through the <see cref="IMessageDispatcher"/>
/// port, so the scheduler carries no transport dependency (DR-009 §5). Hosted by each agent.
/// </summary>
public class CrawlSchedulerService(
    IMessageDispatcher dispatcher,
    CrawlOptions options,
    ILogger<CrawlSchedulerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Targets.Count == 0)
        {
            logger.LogInformation("Crawl scheduler started with no configured targets — idle.");
            return;
        }

        if (!await DelayAsync(options.InitialDelay, stoppingToken)) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var target in options.Targets)
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

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); return true; }
        catch (OperationCanceledException) { return false; }
    }
}
