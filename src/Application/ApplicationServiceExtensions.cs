using FluentValidation;
using Krautwatch.Application.Catalog;
using Krautwatch.Application.Crawling;
using Krautwatch.Application.Downloads;
using Krautwatch.Application.Indexing;
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
        services.AddScoped<CancelDownloadHandler>();
        services.AddScoped<RetryDownloadHandler>();
        services.AddScoped<GetDownloadQueueHandler>();
        services.AddScoped<GetDownloadJobHandler>();

        // Settings
        services.AddScoped<GetSettingsHandler>();
        services.AddScoped<SaveSettingsHandler>();

        // Crawling — the Action handled by the broadcaster agents (Wolverine-discovered)
        services.AddScoped<CrawlShowHandler>();

        // Indexing — the Newznab read side
        services.AddScoped<SearchReleasesHandler>();

        // FluentValidation — all validators in this assembly
        services.AddValidatorsFromAssemblyContaining<SearchCatalogQueryValidator>();

        return services;
    }
}
