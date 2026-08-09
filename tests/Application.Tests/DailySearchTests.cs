using Krautwatch.Application.Indexing;
using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Interfaces;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Krautwatch.Application.Tests;

/// <summary>
/// End-to-end cover for #95: a Sonarr search for a <b>daily</b> episode must find it. Before this,
/// dated episodes were filtered on <c>SeasonNumber</c>/<c>EpisodeNumber</c> — both null by definition —
/// so every such search returned nothing and no dated German show could be grabbed.
/// </summary>
public class DailySearchTests
{
    private readonly IEpisodeRepository _episodes = Substitute.For<IEpisodeRepository>();

    /// <summary>A dated episode: an air date and no numbering, exactly as the crawlers produce.</summary>
    private static Episode Daily(string id, DateTimeOffset broadcast) => new()
    {
        Id = id, Title = $"heute-show vom {broadcast:dd.MM.yyyy}", ShowId = "zdf:heute-show",
        Show = new Show { Id = "zdf:heute-show", Title = "heute-show", ChannelId = "zdf" },
        BroadcastDate = broadcast,
    };

    private static Episode Numbered(string id, int season, int number) => new()
    {
        Id = id, Title = $"Die Biene Maja S{season:00}E{number:00}", ShowId = "kika:biene",
        Show = new Show { Id = "kika:biene", Title = "Die Biene Maja", ChannelId = "kika" },
        BroadcastDate = new DateTimeOffset(2026, 3, 1, 8, 0, 0, TimeSpan.FromHours(1)),
        SeasonNumber = season, EpisodeNumber = number,
    };

    private Task<IReadOnlyList<Release>> Search(SearchReleasesQuery query)
    {
        return new SearchReleasesHandler(_episodes).HandleAsync(query, TestContext.Current.CancellationToken);
    }

    private void GivenHeuteShow()
    {
        // Berlin broadcast times — 20:30 local is 18:30 UTC, which is what makes the timezone handling
        // load-bearing rather than incidental.
        var berlin = TimeSpan.FromHours(2);
        _episodes.SearchAsync("heute-show", Arg.Any<CancellationToken>()).Returns(new List<Episode>
        {
            Daily("zdf:1", new DateTimeOffset(2026, 6, 5, 20, 30, 0, berlin)),
            Daily("zdf:2", new DateTimeOffset(2026, 5, 29, 20, 30, 0, berlin)),
            Daily("zdf:3", new DateTimeOffset(2025, 12, 19, 20, 30, 0, berlin)),
        });
    }

    [Fact]
    public async Task A_daily_episode_search_finds_the_episode_broadcast_that_day()
    {
        GivenHeuteShow();

        // Exactly what Sonarr sends: season is the year, ep is MM/DD.
        var results = await Search(new SearchReleasesQuery(
            Q: "heute-show", Season: 2026, AirDate: new DateOnly(2026, 6, 5)));

        results.Count.ShouldBe(1);
        results[0].Title.ShouldContain("2026-06-05");
    }

    [Fact]
    public async Task A_late_evening_broadcast_is_matched_on_its_local_date()
    {
        // 20:30 Berlin is 18:30 UTC on the same day, but a 01:00 Berlin broadcast is the *previous* day
        // in UTC. Matching on UTC would lose those, so this pins the local-date behaviour.
        var berlin = TimeSpan.FromHours(2);
        _episodes.SearchAsync("nachtshow", Arg.Any<CancellationToken>()).Returns(new List<Episode>
        {
            Daily("zdf:late", new DateTimeOffset(2026, 6, 6, 1, 0, 0, berlin)),
        });

        var results = await Search(new SearchReleasesQuery(
            Q: "nachtshow", Season: 2026, AirDate: new DateOnly(2026, 6, 6)));

        results.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_daily_search_for_a_day_we_do_not_have_returns_nothing()
    {
        GivenHeuteShow();

        var results = await Search(new SearchReleasesQuery(
            Q: "heute-show", Season: 2026, AirDate: new DateOnly(2026, 6, 12)));

        results.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_whole_year_search_returns_that_years_episodes()
    {
        GivenHeuteShow();

        var results = await Search(new SearchReleasesQuery(
            Q: "heute-show", Season: 2026, SeasonOnly: true));

        // Both 2026 broadcasts, not the 2025 one.
        results.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Standard_numbering_still_filters_on_numbers()
    {
        // The regression that matters most: Die Biene Maja matches by SxxExx today and must keep doing so.
        _episodes.SearchAsync("biene", Arg.Any<CancellationToken>()).Returns(new List<Episode>
        {
            Numbered("kika:10", 1, 10),
            Numbered("kika:11", 1, 11),
        });

        var results = await Search(new SearchReleasesQuery(Q: "biene", Season: 1, Episode: 10));

        results.ShouldHaveSingleItem().Title.ShouldContain("S01E10");
    }

    [Fact]
    public async Task A_season_search_still_matches_a_real_season_number()
    {
        // The season-only superset must not break numbered shows: season 1 is a season, not a year.
        _episodes.SearchAsync("biene", Arg.Any<CancellationToken>()).Returns(new List<Episode>
        {
            Numbered("kika:10", 1, 10),
            Numbered("kika:20", 2, 1),
        });

        var results = await Search(new SearchReleasesQuery(Q: "biene", Season: 1, SeasonOnly: true));

        results.ShouldHaveSingleItem();
    }
}
