namespace MediathekNext.Crawlers.Core;

public interface ICrawler
{
    string Source { get; }
    Task<CrawlSummary> CrawlFullAsync(CancellationToken ct = default);
    Task<CrawlSummary> CrawlRecentAsync(int daysPast = 7, CancellationToken ct = default);
}
