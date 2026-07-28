using Krautwatch.Application.Indexing;
using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Enums;
using Krautwatch.Domain.Interfaces;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Krautwatch.Application.Tests;

/// <summary>
/// Covers TVDB-id matching (#4 follow-up). The point of the id is that Sonarr no longer has to identify a
/// series by parsing our release title against notoriously inconsistent Mediathek naming.
/// </summary>
public class TvdbMatchingTests
{
    private static Episode Ep(string id, int? tvdbId, int? season = null, int? episode = null) => new()
    {
        Id = id,
        Title = "an episode",
        ShowId = "ard:show",
        BroadcastDate = DateTimeOffset.UtcNow,
        Duration = TimeSpan.FromMinutes(30),
        SeasonNumber = season,
        EpisodeNumber = episode,
        Show = new Show
        {
            Id = "ard:show", Title = "extra 3", ChannelId = "ard",
            SeriesType = SeriesType.Daily, TvdbId = tvdbId,
        },
    };

    // ── projection ────────────────────────────────────────────

    [Fact]
    public void A_release_carries_the_shows_tvdb_id()
    {
        ReleaseMapper.ToRelease(Ep("ard:1", tvdbId: 255986)).TvdbId.ShouldBe(255986);
    }

    [Fact]
    public void An_unmapped_show_yields_no_tvdb_id_rather_than_a_wrong_one()
    {
        // Most shows are unmapped today. Emitting a bogus id would be far worse than emitting none: Sonarr
        // trusts the id over the title, so a wrong one silently attaches releases to the wrong series.
        ReleaseMapper.ToRelease(Ep("ard:1", tvdbId: null)).TvdbId.ShouldBeNull();
    }

    // ── search by id ──────────────────────────────────────────

    [Fact]
    public async Task A_search_by_tvdb_id_answers_from_the_id_not_the_title()
    {
        var episodes = Substitute.For<IEpisodeRepository>();
        episodes.GetByTvdbIdAsync(255986, Arg.Any<CancellationToken>()).Returns([Ep("ard:1", 255986)]);

        var result = await new SearchReleasesHandler(episodes).HandleAsync(
            new SearchReleasesQuery(TvdbId: 255986), TestContext.Current.CancellationToken);

        result.ShouldHaveSingleItem().TvdbId.ShouldBe(255986);
        await episodes.DidNotReceive().SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unmapped_id_falls_back_to_the_title_search_when_one_was_sent()
    {
        // An id we have not mapped is our gap, not proof the show does not exist — so if Sonarr also sent a
        // title, use it rather than reporting nothing.
        var episodes = Substitute.For<IEpisodeRepository>();
        episodes.GetByTvdbIdAsync(999999, Arg.Any<CancellationToken>()).Returns([]);
        episodes.SearchAsync("extra 3", Arg.Any<CancellationToken>()).Returns([Ep("ard:1", null)]);

        var result = await new SearchReleasesHandler(episodes).HandleAsync(
            new SearchReleasesQuery(Q: "extra 3", TvdbId: 999999), TestContext.Current.CancellationToken);

        result.ShouldHaveSingleItem();
        await episodes.Received(1).SearchAsync("extra 3", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unmapped_id_with_no_title_returns_empty_without_a_title_search()
    {
        var episodes = Substitute.For<IEpisodeRepository>();
        episodes.GetByTvdbIdAsync(999999, Arg.Any<CancellationToken>()).Returns([]);

        var result = await new SearchReleasesHandler(episodes).HandleAsync(
            new SearchReleasesQuery(TvdbId: 999999), TestContext.Current.CancellationToken);

        result.ShouldBeEmpty();
        await episodes.DidNotReceive().SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Season_and_episode_still_narrow_a_search_by_id()
    {
        var episodes = Substitute.For<IEpisodeRepository>();
        episodes.GetByTvdbIdAsync(266275, Arg.Any<CancellationToken>()).Returns(
            [Ep("kika:1", 266275, season: 2, episode: 51),
             Ep("kika:2", 266275, season: 2, episode: 52)]);

        var result = await new SearchReleasesHandler(episodes).HandleAsync(
            new SearchReleasesQuery(Season: 2, Episode: 52, TvdbId: 266275),
            TestContext.Current.CancellationToken);

        result.ShouldHaveSingleItem().Episode.ShouldBe(52);
    }

    [Fact]
    public async Task The_rss_feed_is_unaffected_by_the_id_path()
    {
        // No query and no id: still the recent-releases feed that RSS-Sync polls.
        var episodes = Substitute.For<IEpisodeRepository>();
        episodes.GetRecentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([Ep("ard:1", null)]);

        var result = await new SearchReleasesHandler(episodes).HandleAsync(
            new SearchReleasesQuery(), TestContext.Current.CancellationToken);

        result.ShouldHaveSingleItem();
        await episodes.DidNotReceive().GetByTvdbIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
}
