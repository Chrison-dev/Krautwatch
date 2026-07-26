using System.Linq;
using Krautwatch.Application.Indexing;
using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Enums;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Krautwatch.Application.Tests;

public class ReleaseNamingTests
{
    private static readonly DateTimeOffset Air = new(2026, 7, 10, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Standard_series_gets_an_SxE_title()
    {
        var title = ReleaseNaming.Build("Die Biene Maja", SeriesType.Standard, 2, 52, Air);
        title.ShouldBe("Die.Biene.Maja.S02E52.GERMAN.1080p.WEB.h264");
    }

    [Fact]
    public void Daily_series_gets_an_air_date_title()
    {
        var title = ReleaseNaming.Build("heute-show", SeriesType.Daily, null, null, Air);
        title.ShouldBe("heute-show.2026-07-10.GERMAN.1080p.WEB.h264");
    }

    [Fact]
    public void Standard_without_numbers_falls_back_to_the_air_date()
    {
        var title = ReleaseNaming.Build("extra 3", SeriesType.Standard, null, null, Air);
        title.ShouldBe("extra.3.2026-07-10.GERMAN.1080p.WEB.h264");
    }
}

public class SearchReleasesHandlerTests
{
    private static Episode Ep(string id, SeriesType type, int? season, int? episode, string show = "heute-show") => new()
    {
        Id = id,
        Title = id,
        ShowId = $"zdf:{show}",
        Show = new Show
        {
            Id = $"zdf:{show}", Title = show, ChannelId = "zdf", SeriesType = type,
            Channel = new Channel { Id = "zdf", Name = "ZDF", ProviderKey = "zdf" },
        },
        BroadcastDate = new DateTimeOffset(2026, 7, 10, 20, 0, 0, TimeSpan.Zero),
        Duration = TimeSpan.FromMinutes(30),
        SeasonNumber = season,
        EpisodeNumber = episode,
        ContentType = ContentType.Episode,
    };

    [Fact]
    public async Task Query_maps_episodes_to_releases_with_stable_guid_and_token()
    {
        var repo = Substitute.For<Krautwatch.Domain.Interfaces.IEpisodeRepository>();
        repo.SearchAsync("heute-show", Arg.Any<CancellationToken>())
            .Returns(new[] { Ep("zdf:doc-1", SeriesType.Daily, null, null) });

        var releases = await new SearchReleasesHandler(repo).HandleAsync(new SearchReleasesQuery("heute-show"));

        var release = releases.ShouldHaveSingleItem();
        release.Guid.ShouldBe("zdf:doc-1");
        release.DownloadToken.ShouldBe("zdf:doc-1");
        release.Category.ShouldBe(NewznabCategory.Tv);
        release.Title.ShouldBe("heute-show.2026-07-10.GERMAN.1080p.WEB.h264");
    }

    [Fact]
    public async Task Empty_query_reads_the_recent_feed()
    {
        var repo = Substitute.For<Krautwatch.Domain.Interfaces.IEpisodeRepository>();
        repo.GetRecentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[] { Ep("zdf:doc-9", SeriesType.Daily, null, null) });

        var releases = await new SearchReleasesHandler(repo).HandleAsync(new SearchReleasesQuery(Q: null));

        releases.ShouldHaveSingleItem().Guid.ShouldBe("zdf:doc-9");
        await repo.DidNotReceive().SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Season_and_episode_filter_a_standard_series()
    {
        var repo = Substitute.For<Krautwatch.Domain.Interfaces.IEpisodeRepository>();
        repo.SearchAsync("Die Biene Maja", Arg.Any<CancellationToken>()).Returns(new[]
        {
            Ep("kika:1", SeriesType.Standard, 2, 51, "Die Biene Maja"),
            Ep("kika:2", SeriesType.Standard, 2, 52, "Die Biene Maja"),
        });

        var releases = await new SearchReleasesHandler(repo)
            .HandleAsync(new SearchReleasesQuery("Die Biene Maja", Season: 2, Episode: 52));

        var release = releases.ShouldHaveSingleItem();
        release.Guid.ShouldBe("kika:2");
        release.Season.ShouldBe(2);
        release.Episode.ShouldBe(52);
        release.Title.ShouldBe("Die.Biene.Maja.S02E52.GERMAN.1080p.WEB.h264");
    }
}
