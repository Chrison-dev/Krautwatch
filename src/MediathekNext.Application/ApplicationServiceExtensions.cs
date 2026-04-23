using FluentValidation;
using MediathekNext.Application.Catalog;
using MediathekNext.Application.Downloads;
using MediathekNext.Application.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace MediathekNext.Application;

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
        services.AddScoped<CancelDownloadHandler>();
        services.AddScoped<RetryDownloadHandler>();
        services.AddScoped<GetDownloadQueueHandler>();
        services.AddScoped<GetDownloadJobHandler>();

        // Settings
        services.AddScoped<GetSettingsHandler>();
        services.AddScoped<SaveSettingsHandler>();

        // FluentValidation — all validators in this assembly
        services.AddValidatorsFromAssemblyContaining<SearchCatalogQueryValidator>();

        return services;
    }
}
