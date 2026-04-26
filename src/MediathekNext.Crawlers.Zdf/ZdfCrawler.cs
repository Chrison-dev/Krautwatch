using MediathekNext.Crawlers.Core;

namespace MediathekNext.Crawlers.Zdf;

/// <summary>
/// ICrawler implementation for ZDF. Delegates to the full/recent handlers.
/// </summary>
public sealed class ZdfCrawler(
    CrawlZdfFullHandler fullHandler,
    CrawlZdfRecentHandler recentHandler) : ICrawler
{
    public string Source => "zdf";

    public Task<CrawlSummary> CrawlFullAsync(CancellationToken ct = default)
        => fullHandler.HandleAsync(new CrawlZdfFullCommand(), ct);

    public Task<CrawlSummary> CrawlRecentAsync(int daysPast = 7, CancellationToken ct = default)
        => recentHandler.HandleAsync(new CrawlZdfRecentCommand(daysPast), ct);
}
