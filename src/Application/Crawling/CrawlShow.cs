using Krautwatch.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace Krautwatch.Application.Crawling;

// ============================================================
// Message
// ============================================================

/// <summary>
/// Crawl one show on one broadcaster. Emitted by <see cref="CrawlSchedulerService"/> and handled in
/// the broadcaster agent by <see cref="CrawlShowHandler"/> over the durable Wolverine bus.
/// </summary>
public record CrawlShowCommand(string ProviderKey, string ShowQuery);

// ============================================================
// Action (IO-driven, DR-009)
// ============================================================

/// <summary>
/// The Crawling <b>Action</b>: orchestrates external IO. It selects the broadcaster crawler by
/// <see cref="IBroadcasterCrawler.ProviderKey"/>, crawls the show through the port (the outside
/// world), then persists the resulting episodes. The broadcaster-specific workflow lives entirely
/// behind the port, so this handler is broadcaster-agnostic.
/// </summary>
public class CrawlShowHandler(
    IEnumerable<IBroadcasterCrawler> crawlers,
    IEpisodeRepository episodes,
    ILogger<CrawlShowHandler> logger)
{
    public async Task HandleAsync(CrawlShowCommand command, CancellationToken ct = default)
    {
        var crawler = crawlers.FirstOrDefault(c =>
            string.Equals(c.ProviderKey, command.ProviderKey, StringComparison.OrdinalIgnoreCase));

        if (crawler is null)
        {
            logger.LogWarning("No crawler registered for provider '{Provider}' — skipping '{Show}'.",
                command.ProviderKey, command.ShowQuery);
            return;
        }

        var crawled = await crawler.CrawlShowAsync(command.ShowQuery, ct);
        if (crawled.Count == 0)
        {
            logger.LogInformation("Crawl '{Show}' on {Provider} returned no streamable episodes.",
                command.ShowQuery, command.ProviderKey);
            return;
        }

        await episodes.UpsertManyAsync(crawled, ct);
        logger.LogInformation("Crawled {Count} episode(s) for '{Show}' on {Provider}.",
            crawled.Count, command.ShowQuery, command.ProviderKey);
    }
}
