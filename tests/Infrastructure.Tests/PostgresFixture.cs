using Krautwatch.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Krautwatch.Infrastructure.Tests;

/// <summary>
/// One Postgres container shared by every repository test class in this assembly. Tests run against
/// the *real* production provider (Npgsql) rather than a SQLite stand-in, so provider-specific
/// behaviour — <c>ExecuteUpdate</c>, concurrency tokens, ordering — matches what ships.
/// </summary>
/// <remarks>
/// Requires a working Docker daemon. Each test class calls <see cref="CreateDatabaseAsync"/> to get
/// its own freshly-created database on the shared server, so classes never see each other's rows.
/// </remarks>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container =
        new PostgreSqlBuilder("postgres:17-alpine").Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    /// <summary>
    /// Creates an isolated database with the schema applied, and returns options bound to it.
    /// </summary>
    public async Task<DbContextOptions<AppDbContext>> CreateDatabaseAsync()
    {
        // Unique per caller so parallel test classes cannot collide.
        var dbName = "kw_" + Guid.NewGuid().ToString("N");

        var builder = new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = dbName,
        };

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(builder.ConnectionString)
            .Options;

        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        return options;
    }
}

/// <summary>
/// Binds <see cref="PostgresFixture"/> to a collection so the container starts once for the whole
/// assembly instead of once per test class.
/// </summary>
[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
