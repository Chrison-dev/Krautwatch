using MediathekNext.Application;
using MediathekNext.Infrastructure;

namespace MediathekNext.Worker.Roles;

/// <summary>
/// Worker role: download execution only.
/// TickerQ runs without the dashboard (dashboard lives on core).
/// Picks up download jobs via TickerQ's EF Core distributed locking —
/// no polling loop, no raw SQL.
/// Scale horizontally: docker compose up --scale worker=N
/// </summary>
public static class WorkerRole
{
    public static void Register(IHostApplicationBuilder builder, DbProviderOptions db)
    {
        builder.Services.AddInfrastructure(db);
        builder.Services.AddApplication();

        // TickerQ without dashboard — only download job functions registered
        builder.Services.AddTickerQJobs(includeDashboard: false);
    }

    public static void Configure(WebApplication app)
    {
        // Worker role has no API endpoints
        // TODO: app.UseTickerQ(); — pending TickerQ integration
    }
}
