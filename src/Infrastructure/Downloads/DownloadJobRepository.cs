using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Enums;
using Krautwatch.Domain.Interfaces;
using Krautwatch.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Krautwatch.Infrastructure.Downloads;

public class DownloadJobRepository(AppDbContext db) : IDownloadJobRepository
{
    public async Task<IReadOnlyList<DownloadJob>> GetAllAsync(CancellationToken ct = default) =>
        await db.DownloadJobs
            .Include(j => j.Episode)
                .ThenInclude(e => e!.Show)
                    .ThenInclude(s => s!.Channel)
            .OrderByDescending(j => j.CreatedAt)
            .ToListAsync(ct);

    public async Task<DownloadJob?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        await db.DownloadJobs
            .Include(j => j.Episode)
                .ThenInclude(e => e!.Show)
                    .ThenInclude(s => s!.Channel)
            .FirstOrDefaultAsync(j => j.Id == id, ct);

    public async Task<IReadOnlyList<DownloadJob>> GetByStatusAsync(DownloadStatus status, CancellationToken ct = default) =>
        await db.DownloadJobs
            .Include(j => j.Episode)
                .ThenInclude(e => e!.Show)
                    .ThenInclude(s => s!.Channel)
            .Where(j => j.Status == status)
            .OrderByDescending(j => j.CreatedAt)
            .ToListAsync(ct);

    public async Task<DownloadJob?> TryClaimNextAsync(string workerId, CancellationToken ct = default)
    {
        // Pick the oldest Queued candidate, then claim it with a conditional UPDATE that only lands
        // while it's still Queued. If another worker got there first the update affects 0 rows and we
        // try the next candidate — an atomic claim without a lock or a concurrency token.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var candidateId = await db.DownloadJobs
                .Where(j => j.Status == DownloadStatus.Queued)
                .OrderBy(j => j.CreatedAt)
                .Select(j => j.Id)
                .FirstOrDefaultAsync(ct);
            if (candidateId == Guid.Empty) return null;

            var claimed = await db.DownloadJobs
                .Where(j => j.Id == candidateId && j.Status == DownloadStatus.Queued)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, DownloadStatus.Downloading)
                    .SetProperty(j => j.WorkerId, workerId)
                    .SetProperty(j => j.StartedAt, DateTimeOffset.UtcNow), ct);

            if (claimed > 0)
                return await GetByIdAsync(candidateId, ct);
        }
        return null;
    }

    public async Task<int> ReclaimStaleAsync(string workerId, CancellationToken ct = default) =>
        await db.DownloadJobs
            .Where(j => j.WorkerId == workerId && j.Status == DownloadStatus.Downloading)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, DownloadStatus.Queued)
                .SetProperty(j => j.WorkerId, (string?)null)
                .SetProperty(j => j.StartedAt, (DateTimeOffset?)null)
                .SetProperty(j => j.ProgressPercent, (double?)null), ct);

    public async Task<IReadOnlyList<DownloadJob>> GetByWorkerIdAsync(
        string workerId, CancellationToken ct = default) =>
        await db.DownloadJobs
            .Where(j => j.WorkerId == workerId && j.Status == DownloadStatus.Downloading)
            .ToListAsync(ct);

    public async Task AddAsync(DownloadJob job, CancellationToken ct = default)
    {
        db.DownloadJobs.Add(job);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(DownloadJob job, CancellationToken ct = default)
    {
        db.DownloadJobs.Update(job);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default) =>
        await db.DownloadJobs.Where(j => j.Id == id).ExecuteDeleteAsync(ct);

    public async Task UpdateProgressAsync(Guid id, double percent, CancellationToken ct = default) =>
        await db.DownloadJobs.Where(j => j.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.ProgressPercent, (double?)percent), ct);

    public async Task<DownloadStatus?> GetStatusAsync(Guid id, CancellationToken ct = default) =>
        await db.DownloadJobs.Where(j => j.Id == id)
            .Select(j => (DownloadStatus?)j.Status)
            .FirstOrDefaultAsync(ct);
}
