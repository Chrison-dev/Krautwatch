using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Krautwatch.Infrastructure.Persistence;

/// <summary>EF-backed resolution cache for query-driven search (#58).</summary>
public class ResolvedQueryRepository(AppDbContext db) : IResolvedQueryRepository
{
    public Task<ResolvedQuery?> GetAsync(string normalisedQuery, CancellationToken ct = default) =>
        db.ResolvedQueries.AsNoTracking().FirstOrDefaultAsync(q => q.Query == normalisedQuery, ct);

    public async Task RecordAsync(ResolvedQuery attempt, CancellationToken ct = default)
    {
        var existing = await db.ResolvedQueries.FirstOrDefaultAsync(q => q.Query == attempt.Query, ct);

        if (existing is null)
        {
            db.ResolvedQueries.Add(attempt);
        }
        else
        {
            existing.LastAttemptedAt = attempt.LastAttemptedAt;
            existing.ResultCount = attempt.ResultCount;
            existing.ProvidersTried = attempt.ProvidersTried;
        }

        await db.SaveChangesAsync(ct);
    }
}
