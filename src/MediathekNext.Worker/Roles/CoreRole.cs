using MediathekNext.Api.Endpoints;
using MediathekNext.Application;
using MediathekNext.Infrastructure;

namespace MediathekNext.Worker.Roles;

/// <summary>
/// Core role: owns the DB, runs catalog refresh + maintenance, serves the API.
/// TickerQ runs here with the dashboard.
/// Download execution handled by worker role containers scaled separately.
/// Only ONE instance of this role should run at a time (owns DB migrations).
/// </summary>
public static class CoreRole
{
    public static void Register(IHostApplicationBuilder builder, DbProviderOptions db)
    {
        builder.Services.AddInfrastructure(db);
        builder.Services.AddApplication();
        builder.Services.AddMediathekViewCatalogProvider();

        // TickerQ with dashboard — catalog refresh + maintenance cron jobs
        // Download jobs also registered here so core can enqueue them;
        // worker role processes them via distributed TickerQ locking.
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
