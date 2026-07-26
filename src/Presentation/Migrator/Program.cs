using Krautwatch.Infrastructure;

// Krautwatch migrator (DR-009) — a run-to-completion resource that owns the EF schema. Aspire runs
// it before any DB consumer (agents / newznab WaitForCompletion), so migration ownership is no
// longer tangled into an app host. It applies migrations and exits.
var builder = Host.CreateApplicationBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("krautwatch")
    ?? "Host=localhost;Port=5432;Database=krautwatch;Username=postgres;Password=postgres";

builder.Services.AddInfrastructure(new DbProviderOptions
{
    Provider = "postgres",
    ConnectionString = connectionString,
});

var app = builder.Build();
await app.MigrateDatabaseAsync();
// Run-to-completion: do not app.Run() — migrations applied, exit 0.
