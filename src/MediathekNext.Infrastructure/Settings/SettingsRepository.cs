using MediathekNext.Domain.Entities;
using MediathekNext.Domain.Interfaces;
using MediathekNext.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MediathekNext.Infrastructure.Settings;

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
