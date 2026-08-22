using Krautwatch.Domain.Interfaces;
using Krautwatch.Domain.Options;
using Krautwatch.Infrastructure.Arr;
using Krautwatch.Infrastructure.Auth;
using Krautwatch.Infrastructure.Catalog;
using Krautwatch.Infrastructure.Crawling.Ard;
using Krautwatch.Infrastructure.Crawling.Zdf;
using Krautwatch.Infrastructure.Downloads;
using Krautwatch.Infrastructure.Jobs;
using Krautwatch.Infrastructure.Messaging;
using Krautwatch.Infrastructure.Persistence;
using Krautwatch.Infrastructure.Proxies;
using Krautwatch.Infrastructure.Secrets;
using Krautwatch.Infrastructure.Settings;
using Krautwatch.Infrastructure.Tvdb;
using Tvdb.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
        services.AddScoped<IShowMappingRepository, ShowMappingRepository>();
        services.AddScoped<IImportedShowHintRepository, ImportedShowHintRepository>();
        services.AddScoped<ILocalCredentialStore, LocalCredentialStore>();
        services.AddSingleton<IPasswordHasher, IdentityPasswordHasher>();

        services.AddSecretResolver();

        return services;
    }

    /// <summary>
    /// Registers the stored-credential resolver, which lets a stored secret be a reference (<c>env:</c> /
    /// <c>file:</c>) rather than the secret itself. Idempotent, because the adapters that need it are
    /// registered by several independent Add* calls.
    /// </summary>
    public static IServiceCollection AddSecretResolver(this IServiceCollection services)
    {
        services.TryAddSingleton<ISecretResolver, SecretResolver>();
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
    /// Registers the ARD-platform crawlers (regular ARD + KiKA) behind the <see cref="IBroadcasterCrawler"/>
    /// port, plus the typed <see cref="ArdCatalogClient"/> HTTP client. Wired by the ARD agent host.
    /// </summary>
    /// <param name="configuration">
    /// Bound from the <c>Ard</c> section (#9) — page size and the per-show episode ceiling. Optional:
    /// omitting it keeps the defaults.
    /// </param>
    public static IServiceCollection AddArdCrawlers(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        var options = new ArdOptions();
        configuration?.GetSection(ArdOptions.SectionName).Bind(options);

        services.AddSingleton(options);
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
    /// <param name="configuration">
    /// Bound from the <c>Zdf</c> section (#13) — chiefly <c>ApiAuthKey</c>, so a rotated key is a
    /// config change rather than a rebuild. Optional: omitting it keeps the shipped default.
    /// </param>
    /// <remarks>
    /// Also registers a health check reporting a rejected key. It lives here rather than in each host
    /// so that every host wiring the ZDF client reports the condition — the alternative is a Newznab
    /// API that resolves ZDF on demand, fails every time, and looks perfectly healthy doing it.
    /// </remarks>
    public static IServiceCollection AddZdfCrawler(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        var options = new ZdfOptions();
        configuration?.GetSection(ZdfOptions.SectionName).Bind(options);

        services.AddSingleton(options);
        services.AddSingleton<ZdfAuthState>();
        services.AddHttpClient<ZdfCatalogClient>();
        services.AddScoped<IBroadcasterCrawler>(sp =>
            new ZdfBroadcasterCrawler(sp.GetRequiredService<ZdfCatalogClient>()));

        services.AddHealthChecks()
            .AddCheck<ZdfAuthHealthCheck>("zdf-auth", tags: ["ready"]);

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
        services.AddSecretResolver();   // the stored API key may be an env:/file: reference
        return services;
    }

    /// <summary>
    /// Registers the TheTVDB read adapter (<see cref="ITvdbCatalog"/>) over the first-party
    /// <c>TvdbClient</c> package. Safe to call with no API key configured — every call then returns
    /// nothing and matching degrades to titles, rather than failing.
    /// </summary>
    public static IServiceCollection AddTvdbCatalog(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var configuredKey = configuration["TvdbConfiguration:ApiKey"];
        var configuredPin = configuration["TvdbConfiguration:Pin"];

        services.AddSecretResolver();   // a stored TVDB key may be an env:/file: reference

        services.AddSingleton(sp => new TvdbApiKeySource(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ISecretResolver>(),
            sp.GetRequiredService<ILogger<TvdbApiKeySource>>(),
            configuredKey,
            configuredPin));

        // Registered *before* AddTvdbClient: the library uses TryAddSingleton for its own provider, so
        // ours wins and the key can be resolved per call rather than fixed at first options read.
        services.AddSingleton<ITokenProvider, DynamicKeyTokenProvider>();
        services.AddHttpClient(nameof(DynamicKeyTokenProvider));
        services.AddMemoryCache();

        // AddTvdbClient calls GetRequiredSection("TvdbConfiguration"), which throws when the section is
        // absent — the normal state for an install that has not configured TVDB. Layering the real
        // configuration over defaults guarantees the section exists while letting real values win.
        var withDefaults = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TvdbConfiguration:BaseUrl"] = "https://api4.thetvdb.com/v4",
                ["TvdbConfiguration:ApiKey"] = string.Empty,
            })
            .AddConfiguration(configuration)
            .Build();

        services.AddTvdbClient(withDefaults);
        services.AddScoped<ITvdbCatalog, TvdbCatalog>();
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
        services.AddSingleton<ISubtitleFetcher, HttpSubtitleFetcher>();   // #20 — sidecar alongside the video
        return services;
    }

    /// <summary>
    /// Registers the egress-proxy selector for geo-restricted downloads (#45). Requires an
    /// <see cref="EgressProxyOptions"/> to be registered by the host. Singleton (it opens its own scope
    /// to reach the scoped proxy repository), so it can be injected into the singleton download engines.
    /// </summary>
    public static IServiceCollection AddEgressProxy(this IServiceCollection services)
    {
        services.AddSecretResolver();                       // the proxy URL may be an env:/file: reference
        services.AddSingleton<EgressSettingsSource>();      // config-over-database precedence
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
    /// How long to keep waiting for the database to accept connections before giving up.
    /// </summary>
    /// <remarks>
    /// Generous because it is only paid once, at startup, and the alternative is worse: a migrator that
    /// exits non-zero because Postgres was two seconds late takes the whole compose stack down with it,
    /// since every other service gates on it completing successfully.
    /// </remarks>
    private static readonly TimeSpan DatabaseWaitTimeout = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Runs EF Core migrations, waiting for the database to become reachable first.
    /// </summary>
    /// <remarks>
    /// The wait is not optional in a container deployment. Compose's <c>depends_on</c> with
    /// <c>service_started</c> waits for the Postgres *container*, not for Postgres to accept
    /// connections — observed live: the migrator raced it and died with "Connection refused", and because
    /// every other service depends on the migrator completing, nothing else ever started. Retrying here
    /// fixes it everywhere rather than only in compose, and also covers a database restart under the
    /// running stack.
    /// </remarks>
    public static async Task MigrateDatabaseAsync(this IHost host)
    {
        using var scope = host.Services.CreateScope();
        var db     = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<AppDbContext>>();

        await WaitForDatabaseAsync(db, logger, host.Services.GetService<IHostApplicationLifetime>());

        logger.LogInformation("Running EF Core migrations...");
        await db.Database.MigrateAsync();
        logger.LogInformation("Migrations complete");
    }

    private static async Task WaitForDatabaseAsync(
        AppDbContext db,
        ILogger logger,
        IHostApplicationLifetime? lifetime)
    {
        var deadline = DateTimeOffset.UtcNow + DatabaseWaitTimeout;
        var delay = TimeSpan.FromSeconds(1);
        var attempt = 0;
        var ct = lifetime?.ApplicationStopping ?? CancellationToken.None;

        while (true)
        {
            attempt++;
            try
            {
                // Open the connection directly rather than using CanConnectAsync. CanConnectAsync answers
                // "can I reach *this database*", and returns false when the server is up but the database
                // does not exist yet — which is the normal state on a first run, since EF creates it a
                // moment later. Waiting on it therefore blocks forever on a perfectly healthy server.
                var connection = db.Database.GetDbConnection();
                await connection.OpenAsync(ct);
                await connection.CloseAsync();

                if (attempt > 1)
                    logger.LogInformation("Database reachable after {Attempts} attempts", attempt);
                return;
            }
            catch (PostgresException missingDatabase) when (missingDatabase.SqlState == "3D000")
            {
                // invalid_catalog_name: the server is up and accepting authenticated connections, the
                // database just is not there. That is exactly what MigrateAsync creates, so stop waiting.
                logger.LogInformation("Server is up; database does not exist yet and will be created");
                return;
            }
            catch (Exception ex) when (DateTimeOffset.UtcNow < deadline)
            {
                // Expected while the database is still starting; only the final failure is worth an error.
                logger.LogDebug(ex, "Database not reachable yet (attempt {Attempt})", attempt);
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                logger.LogError(
                    "Database still unreachable after {Timeout:g}; giving up", DatabaseWaitTimeout);
                throw new TimeoutException(
                    $"The database did not become reachable within {DatabaseWaitTimeout:g}.");
            }

            await Task.Delay(delay, ct);
            // Back off gently: quick early retries catch the common case of a database a second behind,
            // without hammering it for three minutes in the genuinely-broken case.
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 1.5, 10));
        }
    }
}
