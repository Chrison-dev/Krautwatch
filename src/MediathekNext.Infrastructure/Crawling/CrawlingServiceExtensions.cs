using MediathekNext.Crawlers.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediathekNext.Infrastructure.Crawling;

public static class CrawlingServiceExtensions
{
    /// <summary>
    /// Registers the Infrastructure implementations of ICrawlRepository and IRawResponseStore.
    /// Call this alongside AddArdCrawler() / AddZdfCrawler().
    /// </summary>
    public static IServiceCollection AddCrawlerInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<RawResponseStoreOptions>(opts =>
            configuration.GetSection(RawResponseStoreOptions.SectionName).Bind(opts));

        services.AddSingleton<IRawResponseStore, RawResponseStore>();
        services.AddScoped<ICrawlRepository, CrawlRepository>();

        // Core handlers that wrap the interfaces — registered here so crawlers don't depend on Infrastructure
        services.AddScoped<StoreRawResponseHandler>();
        services.AddScoped<PersistCrawlResultHandler>();

        return services;
    }
}
