using Krautwatch.Application;
using Krautwatch.Infrastructure;
using Wolverine;
using Wolverine.Postgresql;

// Krautwatch Zdf agent (DR-009). A microservice host wired to Postgres + durable Wolverine;
// the behaviour is filled in by the Application/Crawling slices in a later increment.
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Postgres connection injected by Aspire (AppHost: agent.WithReference(db)).
var connectionString = builder.Configuration.GetConnectionString("krautwatch")
    ?? "Host=localhost;Port=5432;Database=krautwatch;Username=postgres;Password=postgres";

builder.Services.AddInfrastructure(new DbProviderOptions
{
    Provider = "postgres",
    ConnectionString = connectionString,
});
builder.Services.AddApplication();

// Durable Wolverine (Postgres transport) — the shared message store with the API + other agents.
builder.UseWolverine(opts =>
{
    opts.PersistMessagesWithPostgresql(connectionString);
    opts.Policies.UseDurableLocalQueues();
});

// TODO (#3): register the ZDF crawl Action and schedule it (Application/Crawling/Zdf).

var app = builder.Build();

app.MapDefaultEndpoints(); // /health, /alive from ServiceDefaults

app.Run();
