using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Interfaces;
using Krautwatch.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Krautwatch.Infrastructure.Auth;

/// <summary>EF-backed store for the singleton <see cref="AdminAccount"/> (always Id = 1).</summary>
public class LocalCredentialStore(AppDbContext db) : ILocalCredentialStore
{
    private const int SingletonId = 1;

    public Task<bool> ExistsAsync(CancellationToken ct = default) =>
        db.AdminAccounts.AnyAsync(ct);

    public Task<AdminAccount?> GetAsync(CancellationToken ct = default) =>
        db.AdminAccounts.FirstOrDefaultAsync(a => a.Id == SingletonId, ct);

    public async Task CreateAsync(AdminAccount account, CancellationToken ct = default)
    {
        account.Id = SingletonId;
        db.AdminAccounts.Add(account);

        // The singleton primary key is the real guard: two concurrent setup posts cannot both insert,
        // so the second fails on the unique key rather than silently replacing the first admin.
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(AdminAccount account, CancellationToken ct = default)
    {
        db.AdminAccounts.Update(account);
        await db.SaveChangesAsync(ct);
    }
}
