using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Krautwatch.Infrastructure.Persistence;

/// <summary>
/// Design-time factory used by the EF Core CLI tools (dotnet ef migrations add, etc.)
///
/// Run migrations from the Worker project — it is the single startup project:
///
///   cd src/Krautwatch.Worker
///   dotnet ef migrations add &lt;Name&gt; \
///     --project ../Krautwatch.Infrastructure \
///     --startup-project .
///
///   dotnet ef database update \
///     --project ../Krautwatch.Infrastructure \
///     --startup-project .
///
/// The factory uses a local SQLite file (mediathek-design.db) purely for
/// schema generation — it is never used at runtime.
/// </summary>
public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=mediathek-design.db")
            .Options;

        return new AppDbContext(options);
    }
}
