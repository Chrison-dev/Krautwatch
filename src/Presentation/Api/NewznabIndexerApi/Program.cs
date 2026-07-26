using Krautwatch.Api.NewznabIndexerApi.Endpoints;
using Krautwatch.Application;
using Krautwatch.Infrastructure;

// Krautwatch Newznab indexer (DR-010) — the public *arr-facing surface. Reads the catalog and
// serves the Newznab API (caps / search / tvsearch / RSS). Read-only: it does NOT own EF migrations
// (the internal API / migrator does).
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Postgres connection injected by Aspire (AppHost: newznab.WithReference(db)).
var connectionString = builder.Configuration.GetConnectionString("krautwatch")
    ?? "Host=localhost;Port=5432;Database=krautwatch;Username=postgres;Password=postgres";

builder.Services.AddInfrastructure(new DbProviderOptions
{
    Provider = "postgres",
    ConnectionString = connectionString,
});
builder.Services.AddApplication();

var app = builder.Build();

app.MapDefaultEndpoints(); // /health, /alive from ServiceDefaults
app.MapNewznabEndpoints();

app.Run();
