using Krautwatch.Infrastructure.Crawling;
using Shouldly;
using Xunit;

namespace Krautwatch.Infrastructure.Tests;

public class EpisodeNumberingTests
{
    [Theory]
    [InlineData("Geh nicht, Maja! (S02/E52)", 2, 52)]
    [InlineData("Die Sendung S12E07 – Titel", 12, 7)]
    [InlineData("Show s1e5", 1, 5)]
    [InlineData("Tatort Staffel 3, Folge 12", 3, 12)]
    [InlineData("Serie 2x14 – Untertitel", 2, 14)]
    public void Parses_season_and_episode_from_known_title_shapes(string title, int season, int episode)
    {
        var (s, e) = EpisodeNumbering.Parse(title);
        s.ShouldBe(season);
        e.ShouldBe(episode);
    }

    [Theory]
    [InlineData("extra 3 vom 10.07.2026")]
    [InlineData("heute-show vom 5. Juni 2026")]
    [InlineData("Tagesschau 20:00 Uhr")]
    [InlineData("")]
    [InlineData(null)]
    public void Returns_nulls_for_dated_or_unnumbered_titles(string? title)
    {
        var (s, e) = EpisodeNumbering.Parse(title);
        s.ShouldBeNull();
        e.ShouldBeNull();
    }

    [Fact]
    public void A_bare_year_is_not_mistaken_for_a_season_times_episode()
    {
        // "2026x…" must not parse as season 2026 — the season side is capped at two digits.
        var (s, e) = EpisodeNumbering.Parse("Doku 2026 Rückblick");
        s.ShouldBeNull();
        e.ShouldBeNull();
    }
}
