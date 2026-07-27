using Krautwatch.Infrastructure.Crawling.Ard;
using Krautwatch.Infrastructure.Crawling.Zdf;
using Shouldly;
using Xunit;

namespace Krautwatch.Live.Tests;

/// <summary>
/// Fetches ONE real full episode per show and asserts the complete normalized program data
/// (title, show, broadcaster, air date, duration, synopsis, stream, subtitle). [Live].
/// </summary>
[Trait("Category", "Live")]
public class FullEpisodeFetchTests
{
    private static readonly HttpClient Http = new();

    static FullEpisodeFetchTests() =>
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("Krautwatch/1.0 (+https://github.com/Chrison-dev/Krautwatch)");

    [Fact]
    public async Task Fetches_a_full_Extra3_episode_from_ARD()
    {
        var ard = new ArdCatalogClient(Http);
        var show = await ard.FindShowAsync("Extra 3", ct: TestContext.Current.CancellationToken);
        show.ShouldNotBeNull();
        var episodes = await ard.GetFullEpisodesAsync(show!, TestContext.Current.CancellationToken);
        var full = episodes.First(e => e.Title.Contains("extra 3 vom", StringComparison.OrdinalIgnoreCase));

        var detail = await ard.FetchEpisodeDetailAsync(full, TestContext.Current.CancellationToken);

        detail.ShouldNotBeNull();
        detail!.Title.ShouldContain("extra 3 vom", Case.Insensitive);
        detail.Show.ShouldContain("extra 3", Case.Insensitive);
        detail.Broadcaster.ShouldNotBeNullOrWhiteSpace();
        detail.AirDate.ShouldNotBeNull();
        detail.Duration.ShouldBeGreaterThan(TimeSpan.FromMinutes(20)); // a real full episode
        detail.Synopsis.ShouldNotBeNullOrWhiteSpace();
        detail.StreamUrl.ShouldNotBeNull();
        detail.StreamUrl!.ShouldStartWith("https://");
        detail.SubtitleUrl.ShouldNotBeNull(); // ARD exposes a webvtt subtitle
        detail.GeoRestricted.ShouldBeFalse(); // in-house satire — available worldwide (#45)
    }

    [Fact]
    public async Task Fetches_a_full_BieneMaja_episode_from_KiKA()
    {
        var ard = new ArdCatalogClient(Http);
        var show = await ard.FindShowAsync("Biene Maja", client: "kika", TestContext.Current.CancellationToken);
        show.ShouldNotBeNull();
        var episodes = await ard.GetFullEpisodesAsync(show!, TestContext.Current.CancellationToken);
        var full = episodes.First();

        var detail = await ard.FetchEpisodeDetailAsync(full, TestContext.Current.CancellationToken);

        detail.ShouldNotBeNull();
        detail!.Title.ShouldNotBeNullOrWhiteSpace();
        detail.Show.ShouldContain("Biene Maja", Case.Insensitive);
        detail.Broadcaster.ShouldNotBeNullOrWhiteSpace();
        detail.Duration.ShouldBeGreaterThan(TimeSpan.FromMinutes(5)); // a full ~12 min cartoon episode
        detail.StreamUrl.ShouldNotBeNull();
        detail.StreamUrl!.ShouldStartWith("https://");
        detail.GeoRestricted.ShouldBeTrue(); // licensed cartoon, DACH-only (isGeoBlocked) (#45)
    }

    [Fact]
    public async Task Fetches_a_full_HeuteShow_episode_from_ZDF()
    {
        var zdf = new ZdfCatalogClient(Http);
        var episodes = await zdf.SearchEpisodesAsync("Heute Show", TestContext.Current.CancellationToken);
        var full = episodes.First(e => e.Title.Contains("heute-show vom", StringComparison.OrdinalIgnoreCase));

        var detail = await zdf.FetchEpisodeDetailAsync(full, TestContext.Current.CancellationToken);

        detail.ShouldNotBeNull();
        detail!.Title.ShouldContain("heute-show vom", Case.Insensitive);
        detail.Show.ShouldContain("heute-show", Case.Insensitive);
        detail.Broadcaster.ShouldBe("ZDF");
        detail.AirDate.ShouldNotBeNull();
        detail.Duration.ShouldBeGreaterThan(TimeSpan.FromMinutes(20)); // a real full episode
        detail.Synopsis.ShouldNotBeNullOrWhiteSpace();
        detail.StreamUrl.ShouldNotBeNull();
        detail.StreamUrl!.ShouldStartWith("https://");
        detail.GeoRestricted.ShouldBeFalse(); // PTMD geoLocation "none" — worldwide (#45)
    }
}
