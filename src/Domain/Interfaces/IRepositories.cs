using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Enums;

namespace Krautwatch.Domain.Interfaces;

public interface IDownloadJobRepository
{
    Task<DownloadJob?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<DownloadJob>> GetAllAsync(CancellationToken ct = default);
    Task<IReadOnlyList<DownloadJob>> GetByStatusAsync(DownloadStatus status, CancellationToken ct = default);
    Task<IReadOnlyList<DownloadJob>> GetByWorkerIdAsync(string workerId, CancellationToken ct = default);

    /// <summary>
    /// Atomically claims the oldest Queued job for <paramref name="workerId"/> (Queued → Downloading)
    /// and returns it, or null if none. Safe under concurrent Downloader instances — the claim is a
    /// conditional update, so at most one worker wins a given job.
    /// </summary>
    Task<DownloadJob?> TryClaimNextAsync(string workerId, CancellationToken ct = default);

    /// <summary>
    /// Resets jobs left Downloading by this worker (from a previous crash) back to Queued so they run
    /// again. Called once on Downloader startup.
    /// </summary>
    Task<int> ReclaimStaleAsync(string workerId, CancellationToken ct = default);

    Task AddAsync(DownloadJob job, CancellationToken ct = default);
    Task UpdateAsync(DownloadJob job, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);

    // Progress-only write (doesn't touch Status) so an out-of-band cancel isn't clobbered mid-download.
    Task UpdateProgressAsync(Guid id, double percent, CancellationToken ct = default);
    // Lightweight status read — the Downloader polls this to notice a cancel requested by the UI.
    Task<DownloadStatus?> GetStatusAsync(Guid id, CancellationToken ct = default);
}

public interface IEpisodeRepository
{
    Task<Episode?> GetByIdAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<Episode>> SearchAsync(string query, CancellationToken ct = default);
    // Newest episodes first — the Newznab RSS feed (no query) reads from here.
    Task<IReadOnlyList<Episode>> GetRecentAsync(int limit, CancellationToken ct = default);

    /// <summary>
    /// Episodes of shows mapped to a TVDB id. This is the reliable lookup: Sonarr asks by id, so we answer
    /// by id rather than re-parsing titles.
    /// </summary>
    Task<IReadOnlyList<Episode>> GetByTvdbIdAsync(int tvdbId, CancellationToken ct = default);
    Task<IReadOnlyList<Episode>> GetByChannelAsync(string channelId, ContentType? contentType = null, CancellationToken ct = default);
    Task<IReadOnlyList<Episode>> GetByShowAsync(string showId, CancellationToken ct = default);
    Task<IReadOnlyList<Episode>> GetByContentTypeAsync(ContentType contentType, string? channelId = null, CancellationToken ct = default);
    // Returns shows with episode counts — implementation uses SQL aggregation
    Task<IReadOnlyList<(Show Show, int EpisodeCount, DateTimeOffset? LatestBroadcast)>> GetShowsAsync(string? channelId = null, CancellationToken ct = default);
    Task UpsertManyAsync(IEnumerable<Episode> episodes, CancellationToken ct = default);
}

/// <summary>
/// Persistence for show↔TVDB-id mappings. Separate from the crawl graph on purpose — see
/// <see cref="ShowMapping"/> for why writing these onto <c>Show</c> would not survive a re-crawl.
/// </summary>
public interface IShowMappingRepository
{
    /// <summary>Our shows mapped to a TVDB id, best-trusted first. Empty when the id is unmapped.</summary>
    Task<IReadOnlyList<ShowMapping>> GetByTvdbIdAsync(int tvdbId, CancellationToken ct = default);

    /// <summary>Every mapping for one of our shows — a show can legitimately carry only one.</summary>
    Task<ShowMapping?> GetByShowIdAsync(string showId, CancellationToken ct = default);

    Task<IReadOnlyList<ShowMapping>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Records a mapping, upgrading an existing one in place. An <see cref="MappingProvenance.OperatorConfirmed"/>
    /// mapping is never downgraded by a weaker automatic result — the override exists because the automatic
    /// answer was wrong. Returns the mapping as stored, which may be the pre-existing pinned one.
    /// </summary>
    Task<ShowMapping> UpsertAsync(ShowMapping mapping, CancellationToken ct = default);

    Task DeleteAsync(int tvdbId, string showId, CancellationToken ct = default);
}

/// <summary>Tracks which search terms have already been resolved against the broadcasters (#58).</summary>
public interface IResolvedQueryRepository
{
    Task<ResolvedQuery?> GetAsync(string normalisedQuery, CancellationToken ct = default);

    /// <summary>Records an attempt, inserting or replacing the existing entry for this query.</summary>
    Task RecordAsync(ResolvedQuery attempt, CancellationToken ct = default);
}

public interface ISettingsRepository
{
    Task<AppSettings> GetAsync(CancellationToken ct = default);
    Task SaveAsync(AppSettings settings, CancellationToken ct = default);
}

/// <summary>Cached public egress-proxy candidates for Mode B (#45).</summary>
public interface IProxyRepository
{
    /// <summary>Upserts a refreshed batch by <c>Id</c> (host:port), preserving our feedback columns.</summary>
    Task UpsertBatchAsync(IEnumerable<Proxy> proxies, CancellationToken ct = default);

    /// <summary>Ranked best-first candidates for a country: probed-OK first, then by uptime/speed/recency.</summary>
    Task<IReadOnlyList<Proxy>> GetRankedAsync(string country, int limit, CancellationToken ct = default);

    /// <summary>Records the outcome of a real fetch through a proxy (by URL) for future ranking.</summary>
    Task RecordProbeResultAsync(string proxyUrl, bool ok, CancellationToken ct = default);
}

/// <summary>Fetches egress-proxy candidates from an external public list (e.g. GeoNode).</summary>
public interface IProxyListSource
{
    Task<IReadOnlyList<Proxy>> FetchAsync(CancellationToken ct = default);
}
