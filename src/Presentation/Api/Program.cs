using Krautwatch.Application;
using Krautwatch.Api.Endpoints;
using Krautwatch.Infrastructure;
using Wolverine;
using Wolverine.Postgresql;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Postgres connection string injected by Aspire (AppHost: api.WithReference(db)).
var connectionString = builder.Configuration.GetConnectionString("krautwatch")
    ?? "Host=localhost;Port=5432;Database=krautwatch;Username=postgres;Password=postgres";

builder.Services.AddInfrastructure(new DbProviderOptions
{
    Provider = "postgres",
    ConnectionString = connectionString,
});
builder.Services.AddApplication();
builder.Services.AddOpenApi();

// Wolverine — durable messaging backed by Postgres (DR-009: Postgres transport default,
// RabbitMQ opt-in later). Messages survive restarts via the Postgres-backed store.
builder.UseWolverine(opts =>
{
    opts.PersistMessagesWithPostgresql(connectionString);
    opts.Policies.UseDurableLocalQueues();

    opts.PublishMessage<Krautwatch.Application.Downloads.StartDownloadCommand>()
        .ToLocalQueue("downloads");
});

var app = builder.Build();

// The API owns the DB for now — apply EF migrations at startup.
await app.MigrateDatabaseAsync();

app.MapOpenApi();
app.MapDefaultEndpoints(); // /health, /alive from ServiceDefaults

app.MapCatalogEndpoints();
app.MapDownloadEndpoints();
app.MapSettingsEndpoints();

app.Run();
