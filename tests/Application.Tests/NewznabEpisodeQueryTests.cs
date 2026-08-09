using Krautwatch.Application.Indexing;
using Shouldly;
using Xunit;

namespace Krautwatch.Application.Tests;

/// <summary>
/// Covers reading the standard/daily/season regime off a Newznab request (#95). Every shape here was
/// captured from Sonarr 4.0.19's own trace log against a live instance, not inferred from the spec.
/// </summary>
public class NewznabEpisodeQueryTests
{
    [Fact]
    public void A_daily_episode_query_is_an_air_date()
    {
        // Sonarr: t=tvsearch&tvdbid=234791&season=2026&ep=06/05
        var q = NewznabEpisodeQuery.Parse("2026", "06/05");

        q.AirDate.ShouldBe(new DateOnly(2026, 6, 5));
        q.Episode.ShouldBeNull("06/05 is a date, not an episode number");
        q.IsSeasonOnly.ShouldBeFalse();
    }

    [Fact]
    public void A_standard_episode_query_stays_numbering()
    {
        // Sonarr: t=tvsearch&tvdbid=73518&season=1&ep=10
        var q = NewznabEpisodeQuery.Parse("1", "10");

        q.Season.ShouldBe(1);
        q.Episode.ShouldBe(10);
        q.AirDate.ShouldBeNull();
    }

    [Fact]
    public void A_season_query_carries_no_episode_constraint()
    {
        // Sonarr: t=tvsearch&tvdbid=234791&season=2026   (a whole year, for a dated show)
        var q = NewznabEpisodeQuery.Parse("2026", null);

        q.Season.ShouldBe(2026);
        q.Episode.ShouldBeNull();
        q.AirDate.ShouldBeNull();
        q.IsSeasonOnly.ShouldBeTrue();
    }

    [Theory]
    [InlineData("2026", "6/5", 2026, 6, 5)]     // unpadded
    [InlineData("2026", " 06/05 ", 2026, 6, 5)] // whitespace
    [InlineData("2025", "12/31", 2025, 12, 31)]
    [InlineData("2024", "02/29", 2024, 2, 29)]  // leap day
    public void Daily_forms_are_accepted(string season, string ep, int y, int m, int d) =>
        NewznabEpisodeQuery.Parse(season, ep).AirDate.ShouldBe(new DateOnly(y, m, d));

    [Theory]
    [InlineData("2026", "13/01")]   // month 13
    [InlineData("2026", "02/30")]   // 30 February
    [InlineData("2025", "02/29")]   // not a leap year
    [InlineData("2026", "//")]
    [InlineData("2026", "ab/cd")]
    public void An_impossible_date_is_not_treated_as_one(string season, string ep) =>
        NewznabEpisodeQuery.Parse(season, ep).AirDate.ShouldBeNull();

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("garbage", "rubbish")]
    [InlineData("2026", "06/05/extra")]
    public void Nothing_ever_throws_or_rejects(string? season, string? ep)
    {
        // The critical property. `ep` used to bind as an int, so `06/05` produced HTTP 400 on every
        // daily search — and Sonarr disables an indexer that keeps erroring, which is far worse than
        // returning no results. Whatever arrives, this must degrade to "no constraint".
        var q = Should.NotThrow(() => NewznabEpisodeQuery.Parse(season, ep));

        // A date with trailing junk is not a date we can trust; it must not be invented.
        if (ep == "06/05/extra") q.AirDate.ShouldBeNull();
    }

    [Fact]
    public void A_daily_query_without_a_year_is_not_a_date()
    {
        // The year lives in `season`; without it there is nothing to build a date from.
        NewznabEpisodeQuery.Parse(null, "06/05").AirDate.ShouldBeNull();
    }
}
