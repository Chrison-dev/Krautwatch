using Krautwatch.Application.Indexing;
using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Interfaces;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Krautwatch.Application.Tests;

/// <summary>
/// The RSS feed is what Sonarr's RSS-Sync polls, and after downtime it pages back through history to
/// catch up (#12). These cover what that needs: an offset that is honoured, a total that says when to
/// stop, and guids that stay put so the same episode is not grabbed twice.
/// </summary>
public class RssPagingTests
{
    [Fact]
    public async Task The_feed_pages_in_the_database_rather_than_returning_page_one_forever()
    {
        var episodes = Substitute.For<IEpisodeRepository>();
        episodes.GetRecentAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([Ep("zdf:100")]);
        episodes.CountAsync(Arg.Any<CancellationToken>()).Returns(250);

        var page = await new SearchReleasesHandler(episodes).HandleAsync(
            new SearchReleasesQuery(Q: null, Limit: 50, Offset: 100),
            TestContext.Current.CancellationToken);

        // Pushed down to SQL — the alternative is reading the whole catalog to throw most of it away.
        await episodes.Received(1).GetRecentAsync(100, 50, Arg.Any<CancellationToken>());

        page.Offset.ShouldBe(100);
        page.Total.ShouldBe(250);
        page.Releases.ShouldHaveSingleItem().Guid.ShouldBe("zdf:100");
    }

    [Fact]
    public async Task A_search_reports_a_total_that_does_not_send_a_client_paging_into_nothing()
    {
        var episodes = Substitute.For<IEpisodeRepository>();
        episodes.SearchAsync("heute-show", Arg.Any<CancellationToken>())
            .Returns([Ep("zdf:1"), Ep("zdf:2")]);

        var page = await new SearchReleasesHandler(episodes).HandleAsync(
            new SearchReleasesQuery("heute-show"), TestContext.Current.CancellationToken);

        // We cap a search at limit without counting the rest, so the honest answer is "this is all" —
        // claiming more would have a client requesting pages we never promised to serve.
        page.Total.ShouldBe(2);
        page.Offset.ShouldBe(0);
    }

    [Fact]
    public async Task An_offset_past_the_end_is_an_empty_page_not_a_wrapped_one()
    {
        var episodes = Substitute.For<IEpisodeRepository>();
        episodes.GetRecentAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);
        episodes.CountAsync(Arg.Any<CancellationToken>()).Returns(10);

        var page = await new SearchReleasesHandler(episodes).HandleAsync(
            new SearchReleasesQuery(Q: null, Offset: 5000), TestContext.Current.CancellationToken);

        page.Releases.ShouldBeEmpty();
        page.Total.ShouldBe(10);
    }

    [Fact]
    public async Task Offset_applies_to_searches_too_rather_than_being_ignored()
    {
        var episodes = Substitute.For<IEpisodeRepository>();
        episodes.SearchAsync("maja", Arg.Any<CancellationToken>())
            .Returns([Ep("kika:1"), Ep("kika:2"), Ep("kika:3")]);

        var page = await new SearchReleasesHandler(episodes).HandleAsync(
            new SearchReleasesQuery("maja", Offset: 2), TestContext.Current.CancellationToken);

        // Silently ignoring offset is the failure this issue is about: a client paging forward gets
        // the same rows back and either loops or re-grabs them.
        page.Releases.ShouldHaveSingleItem().Guid.ShouldBe("kika:3");
        page.Offset.ShouldBe(2);
    }

    [Fact]
    public async Task A_guid_is_the_episode_id_so_it_survives_a_re_crawl()
    {
        var episodes = Substitute.For<IEpisodeRepository>();
        episodes.GetRecentAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([Ep("zdf:content-documents-heute-show-42")]);
        episodes.CountAsync(Arg.Any<CancellationToken>()).Returns(1);

        var page = await new SearchReleasesHandler(episodes).HandleAsync(
            new SearchReleasesQuery(Q: null), TestContext.Current.CancellationToken);

        // Episode ids are "{provider}:{nativeId}" and upserted in place, so a re-crawl of the same
        // episode reuses this guid — which is what stops Sonarr grabbing it a second time.
        page.Releases.ShouldHaveSingleItem().Guid.ShouldBe("zdf:content-documents-heute-show-42");
    }

    private static Episode Ep(string id) => new()
    {
        Id = id,
        Title = id,
        ShowId = "zdf:heute-show",
        Show = new Show { Id = "zdf:heute-show", Title = "heute-show", ChannelId = "zdf" },
        BroadcastDate = DateTimeOffset.UtcNow,
        Duration = TimeSpan.FromMinutes(30),
    };
}
