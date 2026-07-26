using Krautwatch.Domain.Interfaces;
using Krautwatch.Infrastructure.Catalog;
using Krautwatch.Infrastructure.Catalog.MediathekView;
using Krautwatch.Infrastructure.Crawling.Ard;
using Krautwatch.Infrastructure.Crawling.Zdf;
using Krautwatch.Infrastructure.Downloads;
using Krautwatch.Infrastructure.Jobs;
using Krautwatch.Infrastructure.Messaging;
using Krautwatch.Infrastructure.Persistence;
using Krautwatch.Infrastructure.Settings;
using Krautwatch.Infrastructure.System;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Krautwatch.Infrastructure;

// ──────────────────────────────────────────────────────────────
// Database provider options — swap by changing config only (DR-009).
// Postgres is the default; sqlite/mssql remain available.
// ──────────────────────────────────────────────────────────────

public record DbProviderOptions
{
    public string Provider { get; init; } = "postgres";
    public string ConnectionString { get; init; } =
        "Host=localhost;Port=5432;Database=krautwatch;Username=postgres;Password=postgres";
}

public static class InfrastructureServiceExtensions
{
    /// <summary>
    /// Registers EF Core (the configured provider), the download-queue port, and repositories.
    /// </summary>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        DbProviderOptions dbOptions)
    {
        services.AddDbContext<AppDbContext>(options => ConfigureProvider(options, dbOptions));

        // System status — singleton, written by jobs/agents, read by the API
        services.AddSingleton<SystemStatusService>();

        // File naming for downloads
        services.AddSingleton<FileNamingService>();

        // Download-queue port — the Application layer talks to this abstraction
        services.AddScoped<IDownloadQueue, NullDownloadQueue>();

        // Dispatch port (DR-009 §5) — Wolverine adapter; the transport is configured at host level.
        services.AddScoped<IMessageDispatcher, WolverineDispatcher>();

        // Repositories
        services.AddScoped<IEpisodeRepository, EpisodeRepository>();
        services.AddScoped<IDownloadJobRepository, DownloadJobRepository>();
        services.AddScoped<ISettingsRepository, SettingsRepository>();

        return services;
    }

    private static void ConfigureProvider(DbContextOptionsBuilder options, DbProviderOptions db)
    {
        switch (db.Provider.ToLowerInvariant())
        {
            case "postgres":
            case "postgresql":
                options.UseNpgsql(db.ConnectionString);
                break;
            case "sqlite":
                options.UseSqlite(db.ConnectionString);
                break;
            case "mssql":
            case "sqlserver":
                options.UseSqlServer(db.ConnectionString);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported database provider: '{db.Provider}'. " +
                    "Supported values: postgres, sqlite, mssql");
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

        services.AddScoped<FilmlisteParser>();
        services.AddScoped<ICatalogProvider, MediathekViewProvider>();

        return services;
    }

    /// <summary>
    /// Registers the ARD-platform crawlers (regular ARD + KiKA) behind the <see cref="IBroadcasterCrawler"/>
    /// port, plus the typed <see cref="ArdCatalogClient"/> HTTP client. Wired by the ARD agent host.
    /// </summary>
    public static IServiceCollection AddArdCrawlers(this IServiceCollection services)
    {
        services.AddHttpClient<ArdCatalogClient>();
        services.AddScoped<IBroadcasterCrawler>(sp =>
            new ArdBroadcasterCrawler(sp.GetRequiredService<ArdCatalogClient>(),
                providerKey: "ard", scope: "ard", channelName: "ARD"));
        services.AddScoped<IBroadcasterCrawler>(sp =>
            new ArdBroadcasterCrawler(sp.GetRequiredService<ArdCatalogClient>(),
                providerKey: "kika", scope: "kika", channelName: "KiKA"));
        return services;
    }

    /// <summary>
    /// Registers the ZDF crawler behind the <see cref="IBroadcasterCrawler"/> port, plus the typed
    /// <see cref="ZdfCatalogClient"/> HTTP client. Wired by the ZDF agent host.
    /// </summary>
    public static IServiceCollection AddZdfCrawler(this IServiceCollection services)
    {
        services.AddHttpClient<ZdfCatalogClient>();
        services.AddScoped<IBroadcasterCrawler>(sp =>
            new ZdfBroadcasterCrawler(sp.GetRequiredService<ZdfCatalogClient>()));
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
