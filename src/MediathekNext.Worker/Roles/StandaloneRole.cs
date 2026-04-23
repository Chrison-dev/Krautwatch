using MediathekNext.Api.Endpoints;
using MediathekNext.Application;
using MediathekNext.Infrastructure;

namespace MediathekNext.Worker.Roles;

/// <summary>
/// Standalone role: everything in one process.
/// Runs DB migrations, catalog refresh, TickerQ (with dashboard), download jobs, and API.
/// Ideal for single-machine deployments and local dev.
/// </summary>
public static class StandaloneRole
{
    public static void Register(IHostApplicationBuilder builder, DbProviderOptions db)
    {
        builder.Services.AddInfrastructure(db);
        builder.Services.AddApplication();
        builder.Services.AddMediathekViewCatalogProvider();

        // TickerQ with dashboard — all jobs (catalog, downloads, maintenance)
        builder.Services.AddTickerQJobs(includeDashboard: true);

        if (builder is WebApplicationBuilder web)
            web.Services.AddOpenApi();
    }

    public static void Configure(WebApplication app)
    {
        app.MapOpenApi();
        app.MapDefaultEndpoints();
        app.MapCatalogEndpoints();
        app.MapDownloadEndpoints();
        app.MapSettingsEndpoints();
        app.MapSystemEndpoints();

        // TODO: app.UseTickerQ(); — pending TickerQ integration
    }
}
