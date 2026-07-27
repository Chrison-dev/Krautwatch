using Krautwatch.Domain.Interfaces;
using Krautwatch.Domain.Options;
using Krautwatch.Infrastructure.Arr;
using Krautwatch.Infrastructure.Auth;
using Krautwatch.Infrastructure.Catalog;
using Krautwatch.Infrastructure.Catalog.MediathekView;
using Krautwatch.Infrastructure.Crawling.Ard;
using Krautwatch.Infrastructure.Crawling.Zdf;
using Krautwatch.Infrastructure.Downloads;
using Krautwatch.Infrastructure.Jobs;
using Krautwatch.Infrastructure.Messaging;
using Krautwatch.Infrastructure.Persistence;
using Krautwatch.Infrastructure.Proxies;
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
// Postgres is the default; mssql remains available.
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

        // Repositories
        services.AddScoped<IEpisodeRepository, EpisodeRepository>();
        services.AddScoped<IDownloadJobRepository, DownloadJobRepository>();
        services.AddScoped<ISettingsRepository, SettingsRepository>();
        services.AddScoped<IProxyRepository, ProxyRepository>();

        // Auth — local credential store + password hashing behind Domain ports (#48).
        services.AddScoped<IArrInstanceRepository, ArrInstanceRepository>();
        services.AddScoped<IResolvedQueryRepository, ResolvedQueryRepository>();
        services.AddScoped<ILocalCredentialStore, LocalCredentialStore>();
        services.AddSingleton<IPasswordHasher, IdentityPasswordHasher>();

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
            case "mssql":
            case "sqlserver":
                options.UseSqlServer(db.ConnectionString);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported database provider: '{db.Provider}'. " +
                    "Supported values: postgres, mssql");
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
    /// Registers the outbound Sonarr/Radarr client (#4). Needed by any host that tests instance
    /// connectivity or (per #6) pre-warms the crawl list from a monitored-series list.
    /// </summary>
    public static IServiceCollection AddArrClient(this IServiceCollection services)
    {
        services.AddHttpClient<IArrClient, ArrHttpClient>(http =>
        {
            // Short and explicit: this sits behind an operator clicking "Test", so a hung connection has
            // to fail fast rather than leave the button spinning. HttpClient's 100s default is far too
            // long for that.
            http.Timeout = TimeSpan.FromSeconds(10);
        });
        return services;
    }

    /// <summary>
    /// Registers the Wolverine-backed <see cref="IMessageDispatcher"/> (DR-009 §5). Call only from a
    /// host that has configured Wolverine (<c>UseWolverine</c>) — i.e. the crawl agents.
    /// </summary>
    public static IServiceCollection AddMessageDispatcher(this IServiceCollection services)
    {
        services.AddScoped<IMessageDispatcher, WolverineDispatcher>();
        return services;
    }

    /// <summary>
    /// Registers the download engines for the Downloader agent: a raw progressive-MP4 puller and an
    /// ffmpeg HLS remuxer, behind a dispatcher that routes each job by its stream type.
    /// </summary>
    public static IServiceCollection AddDownloadProvider(this IServiceCollection services)
    {
        services.AddSingleton<RawMp4DownloadProvider>();
        services.AddSingleton<FfmpegDownloadProvider>();
        services.AddSingleton<IDownloadProvider, DownloadDispatcher>();
        return services;
    }

    /// <summary>
    /// Registers the egress-proxy selector for geo-restricted downloads (#45). Requires an
    /// <see cref="EgressProxyOptions"/> to be registered by the host. Singleton (it opens its own scope
    /// to reach the scoped proxy repository), so it can be injected into the singleton download engines.
    /// </summary>
    public static IServiceCollection AddEgressProxy(this IServiceCollection services)
    {
        services.AddSingleton<IEgressProxyProvider, EgressProxyProvider>();
        return services;
    }

    /// <summary>
    /// Registers the public proxy-list source (Mode B, #45): the typed GeoNode HTTP client behind the
    /// <see cref="IProxyListSource"/> port. Requires a <see cref="ProxyListOptions"/> to be registered.
    /// The refresh scheduler + Action are wired by the host (they are Application types).
    /// </summary>
    public static IServiceCollection AddProxyListSource(this IServiceCollection services)
    {
        services.AddHttpClient<IProxyListSource, GeoNodeProxyListSource>();
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
