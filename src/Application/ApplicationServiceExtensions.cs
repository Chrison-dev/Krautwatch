using FluentValidation;
using Krautwatch.Application.Catalog;
using Krautwatch.Application.Crawling;
using Krautwatch.Application.Downloads;
using Krautwatch.Application.Indexing;
using Krautwatch.Application.Auth;
using Krautwatch.Application.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace Krautwatch.Application;

public static class ApplicationServiceExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        // Catalog
        services.AddScoped<SearchCatalogQueryHandler>();
        services.AddScoped<GetEpisodeDetailQueryHandler>();
        services.AddScoped<GetShowsQueryHandler>();
        services.AddScoped<GetShowEpisodesQueryHandler>();
        services.AddScoped<BrowseByChannelQueryHandler>();
        services.AddScoped<BrowseByContentTypeQueryHandler>();

        // Downloads
        services.AddScoped<StartDownloadHandler>();
        services.AddScoped<AddDownloadByTokenHandler>();
        // NB: RunDownloadHandler is registered by the Downloader host only — it needs IDownloadProvider,
        // which no other host provides.
        services.AddScoped<CancelDownloadHandler>();
        services.AddScoped<RetryDownloadHandler>();
        services.AddScoped<DeleteDownloadHandler>();
        services.AddScoped<GetDownloadQueueHandler>();
        services.AddScoped<GetDownloadJobHandler>();

        // Settings
        services.AddScoped<GetSettingsHandler>();
        services.AddScoped<SaveSettingsHandler>();
        // Instance CRUD only needs IArrInstanceRepository, which every host gets from AddInfrastructure,
        // so these are safe here.
        services.AddScoped<GetArrInstancesHandler>();
        services.AddScoped<SaveArrInstanceHandler>();
        services.AddScoped<DeleteArrInstanceHandler>();

        // NB: TestArrConnectionHandler is registered by the Web host only — it needs IArrClient, which
        // only a host that calls AddArrClient() provides. Registering it here made every host fail to
        // start under DI validate-on-build (i.e. in Development).

        // Auth (#48) — SetupToken is a singleton so it survives for the process lifetime and is
        // logged once at startup; the handlers are scoped like every other use-case.
        services.AddSingleton<SetupToken>();
        services.AddScoped<SignInHandler>();
        services.AddScoped<CreateAdminHandler>();
        services.AddScoped<SetupStateHandler>();

        // Crawling — the Action handled by the broadcaster agents (Wolverine-discovered)
        services.AddScoped<CrawlShowHandler>();

        // Indexing — the Newznab read side
        services.AddScoped<SearchReleasesHandler>();

        // FluentValidation — all validators in this assembly
        services.AddValidatorsFromAssemblyContaining<SearchCatalogQueryValidator>();

        return services;
    }

    /// <summary>
    /// Enables query-driven search (#58 / DR-011): a Newznab search for a show no crawler has visited yet
    /// resolves it on demand. Call only from a host that also registers broadcaster crawlers — without them
    /// there is nothing to resolve against.
    /// </summary>
    public static IServiceCollection AddOnDemandResolution(
        this IServiceCollection services, OnDemandResolutionOptions options)
    {
        // Singletons: the coalescing state and the queue must be shared process-wide, which is also why
        // OnDemandResolver takes IServiceScopeFactory rather than a scoped repository.
        services.AddSingleton(options);
        services.AddSingleton<OnDemandResolver>();
        services.AddHostedService<OnDemandResolutionService>();
        return services;
    }

    /// <summary>
    /// Enables TVDB-id matching (PR 3a): resolve the id Sonarr sends, match it onto our catalog, and derive
    /// the season/episode numbering its searches require.
    /// </summary>
    /// <remarks>
    /// Call only from a host that also calls <c>AddTvdbCatalog</c>. Registering this unconditionally would
    /// break DI validate-on-build in every host without an <c>ITvdbCatalog</c> — the same mistake already
    /// made once with the `*arr` client, so the two are paired deliberately rather than folded into
    /// <c>AddApplication</c>. <see cref="SearchReleasesHandler"/> takes the resolver as an optional
    /// parameter, so a host that skips this keeps the pre-PR-3a behaviour exactly.
    /// </remarks>
    public static IServiceCollection AddTvdbMatching(this IServiceCollection services)
    {
        services.AddScoped<TvdbShowResolver>();
        return services;
    }
}
