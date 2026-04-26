using Microsoft.Extensions.DependencyInjection;

namespace MediathekNext.Crawlers.Zdf;

public static class ZdfCrawlerServiceExtensions
{
    /// <summary>
    /// Registers the ZDF crawler and all its handlers.
    /// Requires ICrawlRepository and IRawResponseStore to be registered separately (via Infrastructure).
    /// </summary>
    public static IServiceCollection AddZdfCrawler(this IServiceCollection services)
    {
        services.AddHttpClient<IZdfClient, ZdfClient>();
        services.AddSingleton<ParseZdfEpisodeHandler>();
        services.AddScoped<CrawlZdfFullHandler>();
        services.AddScoped<CrawlZdfRecentHandler>();
        services.AddScoped<ZdfCrawler>();
        return services;
    }
}
