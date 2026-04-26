using Microsoft.Extensions.DependencyInjection;

namespace MediathekNext.Crawlers.Ard;

public static class ArdCrawlerServiceExtensions
{
    /// <summary>
    /// Registers the ARD crawler and all its handlers.
    /// Requires ICrawlRepository and IRawResponseStore to be registered separately (via Infrastructure).
    /// </summary>
    public static IServiceCollection AddArdCrawler(this IServiceCollection services)
    {
        services.AddHttpClient<IArdClient, ArdClient>();
        services.AddSingleton<ParseArdEpisodeHandler>();
        services.AddScoped<CrawlArdFullHandler>();
        services.AddScoped<CrawlArdRecentHandler>();
        services.AddScoped<ArdCrawler>();
        return services;
    }
}
