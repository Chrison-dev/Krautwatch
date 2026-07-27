using Krautwatch.Api.NewznabIndexerApi.Endpoints;
using Krautwatch.Application;
using Krautwatch.Application.Indexing;
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

// Query-driven search (#58 / DR-011). Sonarr searching for a show no crawler has visited must not get an
// empty feed, so this host resolves against the broadcasters on demand — which means it needs the crawler
// clients that until now only the agents had. That makes it run an IO-driven Action, the same narrow DR-009
// deviation recorded for TestArrConnection: a synchronous request cannot wait on the durable bus.
var resolutionOptions = new OnDemandResolutionOptions();
builder.Configuration.GetSection(OnDemandResolutionOptions.SectionName).Bind(resolutionOptions);

if (resolutionOptions.Enabled)
{
    builder.Services.AddArdCrawlers(); // ARD + KiKA
    builder.Services.AddZdfCrawler();
    builder.Services.AddOnDemandResolution(resolutionOptions);
}

var app = builder.Build();

app.MapDefaultEndpoints(); // /health, /alive from ServiceDefaults
app.MapNewznabEndpoints();  // indexer: t=caps|search|tvsearch + /download
app.MapSabnzbdEndpoints();  // download client: mode=version|get_config|addfile|addurl|queue|history

app.Run();
