using Krautwatch.Domain.Entities;

namespace Krautwatch.Domain.Interfaces;

/// <summary>
/// A per-broadcaster crawler port (DR-009 / DR-010). Each broadcaster's Infrastructure adapter
/// encapsulates the site-specific workflow (find show → list episodes → resolve stream) and returns
/// fully-formed <see cref="Episode"/> graphs (Show + Channel + Streams attached) ready to upsert.
/// The Application <c>Crawling</c> Action selects the right crawler by <see cref="ProviderKey"/>.
/// </summary>
public interface IBroadcasterCrawler
{
    /// <summary>The catalog scope this crawler serves — matches <see cref="Channel.ProviderKey"/>.</summary>
    string ProviderKey { get; }

    /// <summary>
    /// Crawl a single show by (a substring of) its title and return its episodes as Domain entities.
    /// Returns an empty list when the show can't be found. IO-driven: this is the external orchestration.
    /// </summary>
    Task<IReadOnlyList<Episode>> CrawlShowAsync(string showQuery, CancellationToken ct = default);
}
