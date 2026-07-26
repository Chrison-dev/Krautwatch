using Krautwatch.Application;
using Krautwatch.Infrastructure;
using Krautwatch.Web.Components;

// Krautwatch standalone UI (Blazor Server). A first-party console to search the catalog, queue a
// download, and monitor progress — usable without a Sonarr/Radarr instance. Talks to the Application
// layer in-process; the Downloader agent does the actual pulling. Does NOT own EF migrations.
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var connectionString = builder.Configuration.GetConnectionString("krautwatch")
    ?? "Host=localhost;Port=5432;Database=krautwatch;Username=postgres;Password=postgres";

builder.Services.AddInfrastructure(new DbProviderOptions
{
    Provider = "postgres",
    ConnectionString = connectionString,
});
builder.Services.AddApplication();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/error", createScopeForErrors: true);

app.UseAntiforgery();

app.MapStaticAssets(); // serves wwwroot + the framework's blazor.web.js (.NET 10 static assets)
app.MapDefaultEndpoints(); // /health, /alive from ServiceDefaults
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();
