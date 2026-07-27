namespace Krautwatch.Domain.Entities;

/// <summary>
/// A record that we have already tried to resolve a search term against the broadcasters, so repeat
/// searches don't re-crawl (#58).
/// </summary>
/// <remarks>
/// The **negative** case is the important one. Sonarr re-issues the same query on a schedule, so without a
/// marker for "we looked and found nothing", every RSS-Sync cycle would trigger a fresh multi-hop crawl of
/// ARD for a show that isn't there. Hence <see cref="ResultCount"/> is recorded rather than just a
/// timestamp: successes and misses are trusted for different lengths of time.
/// </remarks>
public class ResolvedQuery
{
    /// <summary>Normalised search term — lower-cased and whitespace-collapsed. The natural key.</summary>
    public string Query { get; set; } = string.Empty;

    public DateTimeOffset LastAttemptedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Episodes persisted by the last attempt. Zero means a genuine miss, not a failure to run.</summary>
    public int ResultCount { get; set; }

    /// <summary>Which provider keys were tried, for diagnostics when a show is expected but absent.</summary>
    public string? ProvidersTried { get; set; }

    /// <summary>Collapses whitespace and case so trivially different spellings share one cache entry.</summary>
    public static string Normalise(string query) =>
        string.Join(' ', query.Trim().ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
