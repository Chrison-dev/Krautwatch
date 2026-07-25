using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Interfaces;
using Krautwatch.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Krautwatch.Infrastructure.Settings;

/// <summary>
/// Singleton settings — always reads/writes row Id = 1.
/// </summary>
public class SettingsRepository(AppDbContext db) : ISettingsRepository
{
    public async Task<AppSettings> GetAsync(CancellationToken ct = default) =>
        await db.Settings.SingleAsync(ct);

    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        settings.Id = 1; // enforce singleton
        db.Settings.Update(settings);
        await db.SaveChangesAsync(ct);
    }
}
