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
    /// <remarks>
    /// Ordered so the most-trusted candidate comes first: an operator override, then the most-grabbed show,
    /// then oldest. An unattended grab takes the first release offered, so this ordering has to be both
    /// deterministic and aligned with the evidence.
    /// </remarks>
    public async Task<IReadOnlyList<ShowMapping>> GetByTvdbIdAsync(int tvdbId, CancellationToken ct = default) =>
        await db.ShowMappings
            .Include(m => m.Show)
            .Where(m => m.TvdbId == tvdbId)
            .OrderByDescending(m => m.Provenance == MappingProvenance.OperatorConfirmed)
            .ThenByDescending(m => m.PickCount)
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

    public async Task<int> RecordPickAsync(int tvdbId, string showId, CancellationToken ct = default)
    {
        // Increment in the database rather than read-modify-write. Sonarr grabs a whole season in a burst, so
        // concurrent picks of the same show are normal and would otherwise overwrite each other's counts.
        var updated = await db.ShowMappings
            .Where(m => m.TvdbId == tvdbId && m.ShowId == showId)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(m => m.PickCount, m => m.PickCount + 1)
                    .SetProperty(m => m.LastPickedAt, DateTimeOffset.UtcNow)
                    .SetProperty(m => m.UpdatedAt, DateTimeOffset.UtcNow),
                ct);

        if (updated > 0)
        {
            return await db.ShowMappings
                .Where(m => m.TvdbId == tvdbId && m.ShowId == showId)
                .Select(m => m.PickCount)
                .FirstAsync(ct);
        }

        // First pick for a mapping we never offered as a candidate — e.g. a grab of a release from before
        // this feature existed, or a token replayed from an old NZB. Record it rather than dropping the
        // signal; the show is evidently the answer to this id.
        var created = new ShowMapping
        {
            TvdbId = tvdbId,
            ShowId = showId,
            Provenance = MappingProvenance.Learned,
            Evidence = "created by a grab",
            PickCount = 1,
            LastPickedAt = DateTimeOffset.UtcNow,
        };

        db.ShowMappings.Add(created);
        await db.SaveChangesAsync(ct);
        return created.PickCount;
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
