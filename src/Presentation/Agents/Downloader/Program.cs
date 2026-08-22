using Krautwatch.Application;
using Krautwatch.Application.Downloads;
using Krautwatch.Domain.Interfaces;
using Krautwatch.Domain.Options;
using Krautwatch.Infrastructure;
using Krautwatch.Infrastructure.Downloads;

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

builder.Services.AddSingleton<DownloadDirectoryProbe>(); // the setup wizard's writability check (#100)
builder.Services.AddDownloadProvider();                 // the raw-MP4 / ffmpeg download engines
builder.Services.AddEgressProxy();                      // geo-restricted egress selector
builder.Services.AddScoped<RunDownloadHandler>();       // the Action — needs IDownloadProvider (this host only)
builder.Services.AddHostedService<DownloadSupervisor>(); // claims + runs Queued jobs, up to MaxConcurrentDownloads

// Mode B (auto public proxy list) — the source + refresh scheduler. Registered unconditionally: it is
// switchable from the settings UI now (#54), and gating registration on config meant turning it on in
// the UI did nothing until someone restarted the container. The service itself stays idle while off.
builder.Services.AddProxyListSource();               // GeoNode client behind IProxyListSource
builder.Services.AddScoped<RefreshProxyListHandler>();
builder.Services.AddHostedService<ProxyRefreshService>();

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

// The setup wizard's "can you write there?" check (#100). It lives here because this is the only
// service that mounts the media — the Web host deliberately does not, so it has to ask.
//
// `path` is optional: the wizard passes what the operator has just typed, so they can test a path
// before saving it; without it we answer for the directory in force. Reachable only on the compose
// network — the downloader publishes no external endpoint.
app.MapGet("/diagnostics/download-directory", async (
    DownloadDirectoryProbe probe,
    ISettingsRepository settings,
    string? path,
    CancellationToken ct) =>
{
    var target = string.IsNullOrWhiteSpace(path)
        ? (await settings.GetAsync(ct)).DownloadDirectory
        : path;

    return Results.Ok(probe.Check(target));
});

app.Run();
