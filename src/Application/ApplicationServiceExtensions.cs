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
}
