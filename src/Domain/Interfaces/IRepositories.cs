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
    Task<IReadOnlyList<Episode>> GetByChannelAsync(string channelId, ContentType? contentType = null, CancellationToken ct = default);
    Task<IReadOnlyList<Episode>> GetByShowAsync(string showId, CancellationToken ct = default);
    Task<IReadOnlyList<Episode>> GetByContentTypeAsync(ContentType contentType, string? channelId = null, CancellationToken ct = default);
    // Returns shows with episode counts — implementation uses SQL aggregation
    Task<IReadOnlyList<(Show Show, int EpisodeCount, DateTimeOffset? LatestBroadcast)>> GetShowsAsync(string? channelId = null, CancellationToken ct = default);
    Task UpsertManyAsync(IEnumerable<Episode> episodes, CancellationToken ct = default);
}

public interface ISettingsRepository
{
    Task<AppSettings> GetAsync(CancellationToken ct = default);
    Task SaveAsync(AppSettings settings, CancellationToken ct = default);
}
