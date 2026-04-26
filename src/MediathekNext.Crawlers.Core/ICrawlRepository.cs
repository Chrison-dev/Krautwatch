namespace MediathekNext.Crawlers.Core;

/// <summary>
/// Persists a single crawl result to the database immediately after it is parsed.
/// </summary>
public interface ICrawlRepository
{
    Task UpsertAsync(CrawlResult result, CancellationToken ct = default);
}
