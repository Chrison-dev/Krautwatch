using Krautwatch.Infrastructure.Crawling.Ard;
using Shouldly;
using Xunit;

namespace Krautwatch.Live.Tests;

/// <summary>
/// Real-network tests against the live ARD Mediathek API. Tagged [Live] so the default
/// (CI) Test run excludes them — external APIs drift/rate-limit. Run on demand:
///   ./build.cmd TestLive
/// </summary>
[Trait("Category", "Live")]
public class ArdLiveTests
{
    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Krautwatch/1.0 (+https://github.com/Chrison-dev/Krautwatch)");
        return http;
    }
    private static ArdCatalogClient Client => new(Http);

    [Fact]
    public async Task A_show_with_more_episodes_than_the_page_embeds_is_walked_past_the_slice()
    {
        // tagesschau carries widgets in the four figures while the page response embeds a few dozen —
        // the silent truncation #9 is about. Asserted against the cap rather than an exact count,
        // because ARD's catalog changes daily.
        var client = new ArdCatalogClient(Http, new ArdOptions { PageSize = 100, MaxEpisodesPerShow = 250 });

        var show = await client.FindShowAsync("tagesschau", ct: TestContext.Current.CancellationToken);
        show.ShouldNotBeNull();

        var episodes = await client.GetFullEpisodesAsync(show, TestContext.Current.CancellationToken);

        // More than any single page response embeds, and never past the ceiling we set.
        episodes.Count.ShouldBeGreaterThan(100);
        episodes.Count.ShouldBeLessThanOrEqualTo(250);
        episodes.Select(e => e.Id).ShouldBeUnique();
    }

    [Fact]
    public async Task The_cap_is_honoured_against_the_live_catalog()
    {
        var client = new ArdCatalogClient(Http, new ArdOptions { PageSize = 20, MaxEpisodesPerShow = 40 });

        var show = await client.FindShowAsync("tagesschau", ct: TestContext.Current.CancellationToken);
        show.ShouldNotBeNull();

        var episodes = await client.GetFullEpisodesAsync(show, TestContext.Current.CancellationToken);

        episodes.Count.ShouldBeLessThanOrEqualTo(40);
    }

    [Fact]
    public async Task Search_finds_Extra3_on_ARD()
    {
        var show = await Client.FindShowAsync("Extra 3", ct: TestContext.Current.CancellationToken);

        show.ShouldNotBeNull();
        show!.Title.ShouldContain("extra 3", Case.Insensitive);
        show.PageHref.ShouldContain("api.ardmediathek.de");
    }

    [Fact]
    public async Task Fetches_Extra3_full_episodes_with_sane_metadata()
    {
        var show = await Client.FindShowAsync("Extra 3", ct: TestContext.Current.CancellationToken);
        show.ShouldNotBeNull();

        var episodes = await Client.GetFullEpisodesAsync(show!, TestContext.Current.CancellationToken);

        episodes.ShouldNotBeEmpty();
        // Full "extra 3" episodes follow the "extra 3 vom <date>" pattern and run ~30-50 min.
        var fullShow = episodes.FirstOrDefault(e => e.Title.Contains("extra 3 vom", StringComparison.OrdinalIgnoreCase));
        fullShow.ShouldNotBeNull();
        fullShow!.Duration.ShouldBeGreaterThan(TimeSpan.FromMinutes(20));
        fullShow.ShowTitle.ShouldContain("extra 3", Case.Insensitive);
        fullShow.ItemHref.ShouldContain("api.ardmediathek.de");
    }

    [Fact]
    public async Task Downloads_a_full_Extra3_episode()
    {
        var show = await Client.FindShowAsync("Extra 3", ct: TestContext.Current.CancellationToken);
        show.ShouldNotBeNull();
        var episodes = await Client.GetFullEpisodesAsync(show!, TestContext.Current.CancellationToken);
        var full = episodes.First(e => e.Title.Contains("extra 3 vom", StringComparison.OrdinalIgnoreCase));
        var detail = await Client.FetchEpisodeDetailAsync(full, TestContext.Current.CancellationToken);
        detail.ShouldNotBeNull();
        detail!.StreamUrl.ShouldNotBeNull();

        // Raw download of the exact MP4 the ARD Mediathek serves — no conversion, no ffmpeg.
        await Download.VerifyRawMp4Async(Http, detail.StreamUrl!);
    }
}

[Trait("Category", "Live")]
public class ArdKikaLiveTests
{
    private static readonly HttpClient Http = new();
    private static ArdCatalogClient Client => new(Http);

    static ArdKikaLiveTests() =>
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("Krautwatch/1.0 (+https://github.com/Chrison-dev/Krautwatch)");

    [Fact]
    public async Task Search_finds_Biene_Maja_on_KiKA()
    {
        var show = await Client.FindShowAsync("Biene Maja", client: "kika", TestContext.Current.CancellationToken);

        show.ShouldNotBeNull();
        show!.Title.ShouldContain("Biene Maja", Case.Insensitive);
        // KiKA runs on the ARD platform (DR-010 / issue #10) — assert the scoping.
        show.PublicationService.ShouldNotBeNull();
    }

    [Fact]
    public async Task Fetches_Biene_Maja_episodes()
    {
        var show = await Client.FindShowAsync("Biene Maja", client: "kika", TestContext.Current.CancellationToken);
        show.ShouldNotBeNull();
        var episodes = await Client.GetFullEpisodesAsync(show!, TestContext.Current.CancellationToken);
        episodes.ShouldNotBeEmpty();
    }
}
