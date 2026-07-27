using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Krautwatch.Infrastructure.Persistence;

/// <summary>EF-backed store for configured Sonarr/Radarr instances.</summary>
/// <remarks>
/// Listings group by kind then name. Note that <c>Kind</c> is persisted as text, so SQL orders it
/// alphabetically by name (Radarr before Sonarr) rather than by enum value — stable and fine for
/// display, but not the enum's declaration order.
/// </remarks>
public class ArrInstanceRepository(AppDbContext db) : IArrInstanceRepository
{
    public async Task<IReadOnlyList<ArrInstance>> GetAllAsync(CancellationToken ct = default) =>
        await db.ArrInstances
            .OrderBy(i => i.Kind)
            .ThenBy(i => i.Name)
            .AsNoTracking()
            .ToListAsync(ct);

    public Task<ArrInstance?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        db.ArrInstances.FirstOrDefaultAsync(i => i.Id == id, ct);

    public async Task<IReadOnlyList<ArrInstance>> GetEnabledAsync(CancellationToken ct = default) =>
        await db.ArrInstances
            .Where(i => i.Enabled)
            .OrderBy(i => i.Kind)
            .ThenBy(i => i.Name)
            .AsNoTracking()
            .ToListAsync(ct);

    public async Task AddAsync(ArrInstance instance, CancellationToken ct = default)
    {
        db.ArrInstances.Add(instance);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(ArrInstance instance, CancellationToken ct = default)
    {
        db.ArrInstances.Update(instance);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        // ExecuteDelete so removal doesn't need the entity loaded, and is a no-op if it's already gone.
        await db.ArrInstances.Where(i => i.Id == id).ExecuteDeleteAsync(ct);
    }
}
