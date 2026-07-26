using Krautwatch.Agents.Downloader;
using Krautwatch.Application;
using Krautwatch.Application.Downloads;
using Krautwatch.Domain.Interfaces;
using Krautwatch.Domain.Options;
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

// Egress-proxy config for geo-restricted downloads (#45): bind Download:ProxyUrl (bring-your-own) +
// Download:ProxyList (opt-in auto public list). Both sub-options registered so services inject the piece they need.
var egressOptions = new EgressProxyOptions();
builder.Configuration.GetSection(EgressProxyOptions.SectionName).Bind(egressOptions);
builder.Services.AddSingleton(egressOptions);
builder.Services.AddSingleton(egressOptions.ProxyList);

builder.Services.AddDownloadProvider();                 // the raw-MP4 / ffmpeg download engines
builder.Services.AddEgressProxy();                      // geo-restricted egress selector
builder.Services.AddScoped<RunDownloadHandler>();       // the Action — needs IDownloadProvider (this host only)
builder.Services.AddHostedService<DownloadWorker>();    // claims + runs Queued jobs

// Mode B (auto public proxy list) — the source + refresh scheduler, only when enabled.
if (egressOptions.ProxyList.Enabled)
{
    builder.Services.AddProxyListSource();               // GeoNode client behind IProxyListSource
    builder.Services.AddScoped<RefreshProxyListHandler>();
    builder.Services.AddHostedService<ProxyRefreshService>();
}

var app = builder.Build();

// A deployment-configured download directory (the dev fleet points this at a writable temp dir;
// a container mounts /downloads) overrides the seeded default so the Downloader never lands on a
// read-only path like "/downloads" when running as a bare process.
if (app.Configuration["Download:Directory"] is { Length: > 0 } directory)
{
    using var scope = app.Services.CreateScope();
    var settings = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();
    var current = await settings.GetAsync();
    if (current.DownloadDirectory != directory)
    {
        current.DownloadDirectory = directory;
        await settings.SaveAsync(current);
    }
}

app.MapDefaultEndpoints(); // /health, /alive from ServiceDefaults

app.Run();
