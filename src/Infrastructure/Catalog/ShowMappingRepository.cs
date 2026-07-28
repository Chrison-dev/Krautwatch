using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Enums;
using Krautwatch.Domain.Interfaces;
using Krautwatch.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Krautwatch.Infrastructure.Catalog;

/// <summary>
/// EF Core persistence for show↔TVDB-id mappings.
/// </summary>
public class ShowMappingRepository(AppDbContext db) : IShowMappingRepository
{
    /// <remarks>
    /// Ordered strongest-provenance first, then oldest. An unattended grab takes the first candidate, so
    /// the ordering must be deterministic and must favour evidence we trust more.
    /// </remarks>
    public async Task<IReadOnlyList<ShowMapping>> GetByTvdbIdAsync(int tvdbId, CancellationToken ct = default) =>
        await db.ShowMappings
            .Include(m => m.Show)
            .Where(m => m.TvdbId == tvdbId)
            .OrderByDescending(m => m.Provenance == MappingProvenance.OperatorConfirmed)
            .ThenByDescending(m => m.Provenance == MappingProvenance.Learned)
            .ThenBy(m => m.CreatedAt)
            .ThenBy(m => m.ShowId)
            .ToListAsync(ct);

    public async Task<ShowMapping?> GetByShowIdAsync(string showId, CancellationToken ct = default) =>
        await db.ShowMappings
            .Include(m => m.Show)
            .FirstOrDefaultAsync(m => m.ShowId == showId, ct);

    public async Task<IReadOnlyList<ShowMapping>> GetAllAsync(CancellationToken ct = default) =>
        await db.ShowMappings
            .Include(m => m.Show)
            .OrderBy(m => m.ShowId)
            .ToListAsync(ct);

    public async Task<ShowMapping> UpsertAsync(ShowMapping mapping, CancellationToken ct = default)
    {
        // A show maps to at most one series, so "already mapped" is keyed on the show, not on the pair.
        var existing = await db.ShowMappings
            .FirstOrDefaultAsync(m => m.ShowId == mapping.ShowId, ct);

        if (existing is null)
        {
            db.ShowMappings.Add(mapping);
            await db.SaveChangesAsync(ct);
            return mapping;
        }

        // An operator override exists precisely because the automatic answer was wrong. Re-deriving over it
        // would undo the fix, so weaker evidence is refused rather than applied.
        if (existing.IsPinned && mapping.Provenance != MappingProvenance.OperatorConfirmed)
            return existing;

        if (existing.TvdbId != mapping.TvdbId)
        {
            // The key includes TvdbId, so re-pointing a show is a delete plus an insert rather than an
            // update — EF cannot mutate a primary-key column in place.
            db.ShowMappings.Remove(existing);
            await db.SaveChangesAsync(ct);

            db.ShowMappings.Add(mapping);
            await db.SaveChangesAsync(ct);
            return mapping;
        }

        existing.Provenance = mapping.Provenance;
        existing.Evidence = mapping.Evidence;
        existing.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return existing;
    }

    public async Task DeleteAsync(int tvdbId, string showId, CancellationToken ct = default)
    {
        var existing = await db.ShowMappings
            .FirstOrDefaultAsync(m => m.TvdbId == tvdbId && m.ShowId == showId, ct);

        if (existing is null)
            return;

        db.ShowMappings.Remove(existing);
        await db.SaveChangesAsync(ct);
    }
}
