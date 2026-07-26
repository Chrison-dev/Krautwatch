using Krautwatch.Domain.Enums;
using Krautwatch.Infrastructure.Crawling.Ard;
using Krautwatch.Infrastructure.Crawling.Zdf;
using Shouldly;
using Xunit;

namespace Krautwatch.Live.Tests;

/// <summary>
/// Real-network tests for the <c>IBroadcasterCrawler</c> adapters — the full port path a crawl
/// Action runs (find/search → fetch detail → map to Domain <c>Episode</c>). Tagged [Live] so the
/// default (CI) Test run excludes them. Run on demand:
///   ./build.cmd TestLive
/// </summary>
[Trait("Category", "Live")]
public class BroadcasterCrawlerLiveTests
{
    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Krautwatch/1.0 (+https://github.com/Chrison-dev/Krautwatch)");
        return http;
    }

    [Fact]
    public async Task Ard_crawler_maps_Extra3_to_domain_episodes_with_streams()
    {
        var crawler = new ArdBroadcasterCrawler(new ArdCatalogClient(Http), "ard", "ard", "ARD");

        var episodes = await crawler.CrawlShowAsync("Extra 3");

        episodes.ShouldNotBeEmpty();
        var episode = episodes[0];
        episode.Id.ShouldStartWith("ard:");
        episode.Show.Channel.ProviderKey.ShouldBe("ard");
        episode.Show.Id.ShouldStartWith("ard:");

        var stream = episode.Streams.ShouldHaveSingleItem();
        stream.Format.ShouldBe("mp4");
        stream.Quality.ShouldBe(VideoQuality.High);
        stream.Url.ShouldStartWith("http");
    }

    [Fact]
    public async Task Zdf_crawler_maps_HeuteShow_to_domain_episodes_with_streams()
    {
        var crawler = new ZdfBroadcasterCrawler(new ZdfCatalogClient(Http));

        var episodes = await crawler.CrawlShowAsync("heute-show");

        episodes.ShouldNotBeEmpty();
        episodes.ShouldAllBe(e => e.Id.StartsWith("zdf:"));
        episodes[0].Streams.ShouldNotBeEmpty();
    }
}
