using MediathekNext.Crawlers.Core;

namespace MediathekNext.Crawlers.Ard;

/// <summary>
/// ICrawler implementation for ARD. Delegates to the full/recent handlers.
/// </summary>
public sealed class ArdCrawler(
    CrawlArdFullHandler fullHandler,
    CrawlArdRecentHandler recentHandler) : ICrawler
{
    public string Source => "ard";

    public Task<CrawlSummary> CrawlFullAsync(CancellationToken ct = default)
        => fullHandler.HandleAsync(new CrawlArdFullCommand(), ct);

    public Task<CrawlSummary> CrawlRecentAsync(int daysPast = 7, CancellationToken ct = default)
        => recentHandler.HandleAsync(new CrawlArdRecentCommand(daysPast), ct);
}
