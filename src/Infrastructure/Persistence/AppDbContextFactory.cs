using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Krautwatch.Infrastructure.Persistence;

/// <summary>
/// Design-time factory for the EF Core CLI (dotnet ef migrations add ...).
/// Uses Npgsql with a placeholder connection string purely for schema generation —
/// `migrations add` does not connect, so no live Postgres is required to scaffold.
/// </summary>
public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=krautwatch_design;Username=postgres;Password=postgres")
            .Options;

        return new AppDbContext(options);
    }
}
