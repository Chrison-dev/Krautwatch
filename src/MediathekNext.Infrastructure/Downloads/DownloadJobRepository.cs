using MediathekNext.Domain.Entities;
using MediathekNext.Domain.Enums;
using MediathekNext.Domain.Interfaces;
using MediathekNext.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MediathekNext.Infrastructure.Downloads;

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

    public async Task<DownloadJob?> GetNextQueuedAsync(CancellationToken ct = default) =>
        await db.DownloadJobs
            .Include(j => j.Episode)
                .ThenInclude(e => e!.Show)
                    .ThenInclude(s => s!.Channel)
            .Where(j => j.Status == DownloadStatus.Queued)
            .OrderBy(j => j.CreatedAt)
            .FirstOrDefaultAsync(ct);

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
}
