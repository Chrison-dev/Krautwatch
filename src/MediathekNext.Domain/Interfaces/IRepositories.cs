using MediathekNext.Domain.Entities;
using MediathekNext.Domain.Enums;

namespace MediathekNext.Domain.Interfaces;

public interface IDownloadJobRepository
{
    Task<DownloadJob?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<DownloadJob>> GetAllAsync(CancellationToken ct = default);
    Task<IReadOnlyList<DownloadJob>> GetByStatusAsync(DownloadStatus status, CancellationToken ct = default);
    Task AddAsync(DownloadJob job, CancellationToken ct = default);
    Task UpdateAsync(DownloadJob job, CancellationToken ct = default);
}

public interface IEpisodeRepository
{
    Task<Episode?> GetByIdAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<Episode>> SearchAsync(string query, CancellationToken ct = default);
    Task<IReadOnlyList<Episode>> GetByChannelAsync(string channelId, CancellationToken ct = default);
    Task UpsertManyAsync(IEnumerable<Episode> episodes, CancellationToken ct = default);
}

public interface ISettingsRepository
{
    Task<AppSettings> GetAsync(CancellationToken ct = default);
    Task SaveAsync(AppSettings settings, CancellationToken ct = default);
}
