using Krautwatch.Agents.Downloader;
using Krautwatch.Application;
using Krautwatch.Application.Downloads;
using Krautwatch.Infrastructure;

// Krautwatch Downloader agent (DR-009). Polls the durable job table for Queued downloads and pulls
// each stream to disk (raw progressive MP4) via the Application download Action.
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
builder.Services.AddDownloadProvider();                 // the raw-MP4 / ffmpeg download engines
builder.Services.AddScoped<RunDownloadHandler>();       // the Action — needs IDownloadProvider (this host only)
builder.Services.AddHostedService<DownloadWorker>();    // claims + runs Queued jobs

var app = builder.Build();

app.MapDefaultEndpoints(); // /health, /alive from ServiceDefaults

app.Run();
