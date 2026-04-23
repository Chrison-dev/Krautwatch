using MediathekNext.Domain.Interfaces;
using MediathekNext.Infrastructure.Catalog;
using MediathekNext.Infrastructure.Catalog.MediathekView;
using MediathekNext.Infrastructure.Downloads;
using MediathekNext.Infrastructure.Jobs;
using MediathekNext.Infrastructure.Persistence;
using MediathekNext.Infrastructure.Settings;
using MediathekNext.Infrastructure.System;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
// using TickerQ.DependencyInjection; // TODO: Add proper TickerQ reference

namespace MediathekNext.Infrastructure;

// ──────────────────────────────────────────────────────────────
// Database provider options — swap by changing config only
// ──────────────────────────────────────────────────────────────

public record DbProviderOptions
{
    public string Provider { get; init; } = "sqlite";
    public string ConnectionString { get; init; } = "Data Source=/data/mediathek.db";
}

public static class InfrastructureServiceExtensions
{
    /// <summary>
    /// Registers EF Core, repositories, TickerQ, and job classes.
    /// </summary>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        DbProviderOptions dbOptions)
    {
        services.AddDbContext<AppDbContext>(options =>
            ConfigureProvider(options, dbOptions));

        // System status — singleton, written by job classes, read by API
        services.AddSingleton<SystemStatusService>();

        // File naming for downloads
        services.AddSingleton<FileNamingService>();

        // Download queue abstraction — Application layer talks to this, not TickerQ directly
        services.AddScoped<IDownloadQueue, TickerQDownloadQueue>();

        // Repositories
        services.AddScoped<IEpisodeRepository, EpisodeRepository>();
        services.AddScoped<IDownloadJobRepository, DownloadJobRepository>();
        services.AddScoped<ISettingsRepository, SettingsRepository>();

        return services;
    }

    /// <summary>
    /// Registers TickerQ with EF Core persistence, dashboard, and all job classes.
    /// Call this from roles that run jobs: core (catalog + maintenance) + worker (downloads).
    /// In standalone mode, call once — all jobs are registered together.
    /// </summary>
    public static IServiceCollection AddTickerQJobs(
        this IServiceCollection services,
        bool includeDashboard = false)
    {
        // TODO: Properly implement TickerQ integration
        // For now, just return services to allow compilation
        
        /*
        services.AddTickerQ(options =>
        {
            options.SetMaxConcurrency(4);
            options.SetExceptionHandler<DownloadJobExceptionHandler>();

            options.AddOperationalStore<AppDbContext>(efOpts =>
            {
                // UseModelCustomizerForMigrations is NOT used here — we apply
                // TickerQ configs explicitly in AppDbContext.OnModelCreating
                // so they're visible at design-time without extra setup.
                efOpts.CancelMissedTickersOnApplicationRestart();
            });

            if (includeDashboard)
            {
                options.AddDashboard(dashOpts =>
                {
                    dashOpts.BasePath = "/tickerq";
                    // Basic auth credentials from config (TickerQ:Dashboard:Username/Password)
                    dashOpts.AddDashboardBasicAuth();
                });
            }
        });

        // Job classes — registered as scoped by TickerQ's source generator
        services.AddScoped<ResolveStreamJob>();
        services.AddScoped<DownloadStreamJob>();
        services.AddScoped<FinaliseDownloadJob>();
        services.AddScoped<CatalogRefreshJob>();
        services.AddScoped<MaintenanceJobs>();
        services.AddScoped<DownloadJobExceptionHandler>();

        services.AddHttpClient<MediathekViewProvider>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(300);
            client.DefaultRequestHeaders.Add("User-Agent", "MediathekNext/1.0");
        });

        // Seed cron tickers on startup
        services.AddHostedService<TickerQSeedService>();
        */

        return services;
    }

    private static void ConfigureProvider(DbContextOptionsBuilder options, DbProviderOptions db)
    {
        switch (db.Provider.ToLowerInvariant())
        {
            case "sqlite":
                options.UseSqlite(db.ConnectionString);
                break;
            /*
            case "postgres":
            case "postgresql":
                options.UseNpgsql(db.ConnectionString);
                break;
            case "mssql":
            case "sqlserver":
                options.UseSqlServer(db.ConnectionString);
                break;
            */
            default:
                throw new InvalidOperationException(
                    $"Unsupported database provider: '{db.Provider}'. " +
                    "Supported values: sqlite, postgres, mssql");
        }
    }

    /// <summary>
    /// Registers the MediathekView catalog provider.
    /// </summary>
    public static IServiceCollection AddMediathekViewCatalogProvider(
        this IServiceCollection services,
        Action<MediathekViewOptions>? configure = null)
    {
        services.AddOptions<MediathekViewOptions>()
            .Configure<IConfiguration>((opts, cfg) =>
                cfg.GetSection(MediathekViewOptions.SectionName).Bind(opts));

        if (configure is not null)
            services.Configure(configure);

        // TODO: Add HTTP client extensions package
        /*
        services.AddHttpClient<MediathekViewProvider>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(300);
            client.DefaultRequestHeaders.Add("User-Agent", "MediathekNext/1.0");
        });
        */

        services.AddScoped<FilmlisteParser>();
        services.AddScoped<ICatalogProvider, MediathekViewProvider>();

        return services;
    }

    /// <summary>
    /// Runs EF Core migrations. Call only from roles that own the DB (core + standalone).
    /// </summary>
    public static async Task MigrateDatabaseAsync(this IHost host)
    {
        using var scope = host.Services.CreateScope();
        var db     = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<AppDbContext>>();
        logger.LogInformation("Running EF Core migrations...");
        await db.Database.MigrateAsync();
        logger.LogInformation("Migrations complete");
    }
}
