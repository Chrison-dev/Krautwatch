using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Interfaces;
using Krautwatch.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Krautwatch.Infrastructure.Catalog;

/// <summary>EF Core persistence for curated mapping hints imported from third-party sets.</summary>
public class ImportedShowHintRepository(AppDbContext db) : IImportedShowHintRepository
{
    public async Task<IReadOnlyList<ImportedShowHint>> GetByTvdbIdAsync(
        int tvdbId,
        CancellationToken ct = default) =>
        await db.ImportedShowHints
            .Where(hint => hint.TvdbId == tvdbId)
            .OrderBy(hint => hint.Topic)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<ImportedShowHint>> GetAllAsync(CancellationToken ct = default) =>
        await db.ImportedShowHints
            .OrderBy(hint => hint.Source)
            .ThenBy(hint => hint.Topic)
            .ToListAsync(ct);

    public async Task<int> ReplaceSourceAsync(
        string source,
        IEnumerable<ImportedShowHint> hints,
        CancellationToken ct = default)
    {
        var incoming = hints
            // The composite key is (TvdbId, NormalizedTopic); a source can legitimately list the same pair
            // twice under different rulesets, and EF would throw on the duplicate.
            .GroupBy(hint => (hint.TvdbId, hint.NormalizedTopic))
            .Select(group => group.First())
            .ToList();

        await db.ImportedShowHints
            .Where(hint => hint.Source == source)
            .ExecuteDeleteAsync(ct);

        if (incoming.Count == 0)
            return 0;

        db.ImportedShowHints.AddRange(incoming);
        await db.SaveChangesAsync(ct);
        return incoming.Count;
    }
}
