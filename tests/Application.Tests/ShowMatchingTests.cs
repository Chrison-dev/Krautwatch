using Krautwatch.Application.Indexing;
using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Enums;
using Krautwatch.Domain.Interfaces;
using Shouldly;
using Xunit;

namespace Krautwatch.Application.Tests;

/// <summary>
/// Covers matching a TVDB series onto our catalog (PR 3a). Every case here is drawn from a real
/// observation against the live TVDB API and a real Sonarr instance on 2026-07-28 — see
/// <c>docs/plans/2026-07-28 - sonarr-radarr-integration.md</c>.
/// </summary>
public class TitleNormalizerTests
{
    [Theory]
    // The case that forces year-stripping: TVDB disambiguates in the title, the Mediathek does not.
    [InlineData("Die Biene Maja (2013)", "die biene maja")]
    [InlineData("Die Biene Maja", "die biene maja")]
    [InlineData("Maya the Bee 2013", "maya the bee")]
    // Mediathek punctuation: a middle dot separating brand from strand subtitle.
    [InlineData("extra 3 · Der Irrsinn der Woche", "extra 3 der irrsinn der woche")]
    [InlineData("heute-show", "heute show")]
    [InlineData("heute show", "heute show")]
    [InlineData("ZDF Magazin Royale", "zdf magazin royale")]
    public void Folds_titles_to_a_comparable_form(string input, string expected) =>
        TitleNormalizer.Normalize(input).ShouldBe(expected);

    [Theory]
    [InlineData("Löwenzahn", "loewenzahn")]
    [InlineData("Für alle Fälle", "fuer alle faelle")]
    [InlineData("Straße", "strasse")]
    [InlineData("Café Éclair", "cafe eclair")]
    public void Folds_umlauts_and_diacritics(string input, string expected) =>
        TitleNormalizer.Normalize(input).ShouldBe(expected);

    [Fact]
    public void A_year_that_is_part_of_the_name_is_kept()
    {
        // Only a *trailing* year is disambiguation noise. "1899" is the whole title of a real series, and
        // stripping it would leave nothing to match on.
        TitleNormalizer.Normalize("1899").ShouldBe("1899");
        TitleNormalizer.Normalize("Berlin 1945 Tagebuch").ShouldBe("berlin 1945 tagebuch");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("···")]
    public void Degenerate_input_normalises_to_empty(string? input) =>
        TitleNormalizer.Normalize(input).ShouldBeEmpty();

    [Fact]
    public void Strips_a_leading_german_article_only_as_a_fallback()
    {
        // The measured case: "Die Sendung mit der Maus" returns nothing from TVDB search, while
        // "Sendung mit der Maus" returns tvdb 153241.
        TitleNormalizer.WithoutLeadingArticle("Die Sendung mit der Maus").ShouldBe("Sendung mit der Maus");
        TitleNormalizer.WithoutLeadingArticle("Der Tatortreiniger").ShouldBe("Tatortreiniger");
        TitleNormalizer.WithoutLeadingArticle("Das Traumschiff").ShouldBe("Traumschiff");
    }

    [Theory]
    [InlineData("Tatort")]                 // no article at all
    [InlineData("Dienstags bei Ingo")]     // starts with "Die"-like prefix but is one word
    [InlineData("")]
    [InlineData(null)]
    public void Reports_no_fallback_when_there_is_no_article(string? input) =>
        TitleNormalizer.WithoutLeadingArticle(input).ShouldBeNull();
}

public class ShowMatcherTests
{
    private static Show Ours(string id, string title, string channelId = "zdf") =>
        new() { Id = id, Title = title, ChannelId = channelId, SeriesType = SeriesType.Daily };

    private static TvdbSeries Tvdb(
        int id, string name, string? network = null, params string[] aliases) =>
        new(id, name, null, network, aliases);

    [Fact]
    public void An_exact_name_match_ranks_highest()
    {
        var candidates = ShowMatcher.Rank(
            Tvdb(234791, "heute-show", "ZDF"),
            [Ours("zdf:heute-show", "heute-show"), Ours("zdf:other", "Bares für Rares")]);

        var best = candidates.First();
        best.Show.Id.ShouldBe("zdf:heute-show");
        best.IsExact.ShouldBeTrue();
    }

    [Fact]
    public void Matches_through_a_german_alias_rather_than_the_english_primary_name()
    {
        // tvdb 73518's primary name is "Maya the Bee"; the Mediathek only ever says "Die Biene Maja".
        // Without alias matching this whole feature does nothing for the shows people actually want.
        var candidates = ShowMatcher.Rank(
            Tvdb(73518, "Maya the Bee", "ZDF", "Die Biene Maja 1975"),
            [Ours("kika:die-biene-maja", "Die Biene Maja", "kika")]);

        candidates.ShouldHaveSingleItem().Show.Id.ShouldBe("kika:die-biene-maja");
    }

    [Fact]
    public void A_mediathek_strand_subtitle_still_matches_the_on_air_brand()
    {
        // Real ARD data: our title carries a strand subtitle the TVDB name does not.
        var candidates = ShowMatcher.Rank(
            Tvdb(255986, "Extra 3", "Norddeutscher Rundfunk (NDR)"),
            [Ours("ard:extra-3-der-irrsinn-der-woche", "extra 3 · Der Irrsinn der Woche", "ard")]);

        var only = candidates.ShouldHaveSingleItem();
        only.Show.Id.ShouldBe("ard:extra-3-der-irrsinn-der-woche");
        only.IsExact.ShouldBeFalse();   // a prefix is strong evidence, but it is not identity
    }

    [Fact]
    public void All_three_of_our_extra_3_variants_are_offered_as_candidates()
    {
        // The case the operator's candidate-fan-out design exists for: one TVDB id, three of our shows.
        var candidates = ShowMatcher.Rank(
            Tvdb(255986, "Extra 3", "Norddeutscher Rundfunk (NDR)"),
            [
                Ours("ard:extra-3-der-irrsinn-der-woche", "extra 3 · Der Irrsinn der Woche", "ard"),
                Ours("zdf:extra-3", "extra 3"),
                Ours("zdf:extra-3-spezial", "extra 3 Spezial: Der reale Irrsinn"),
            ]);

        candidates.Count.ShouldBe(3);
        candidates.First().Show.Id.ShouldBe("zdf:extra-3");   // exact beats the two prefixes
    }

    [Fact]
    public void A_coincidental_substring_is_not_a_match()
    {
        // TVDB search really does return "3-2-1 Contact Extra" for the query "extra 3". Word-boundary
        // matching is what stops that becoming a mapping.
        ShowMatcher.Rank(
            Tvdb(74959, "3-2-1 Contact Extra", "PBS"),
            [Ours("zdf:extra-3", "extra 3")])
        .ShouldBeEmpty();
    }

    [Fact]
    public void An_ard_show_agrees_with_a_regional_member_network()
    {
        // ARD is a federation; TVDB credits NDR, not "Das Erste". Filtering on the national brand alone
        // would drop the match entirely.
        var withMember = ShowMatcher.Rank(
            Tvdb(255986, "extra 3", "Norddeutscher Rundfunk (NDR)"),
            [Ours("ard:extra-3", "extra 3", "ard")]).ShouldHaveSingleItem();

        var withoutNetwork = ShowMatcher.Rank(
            Tvdb(255986, "extra 3", network: null),
            [Ours("ard:extra-3", "extra 3", "ard")]).ShouldHaveSingleItem();

        withMember.Score.ShouldBeGreaterThan(withoutNetwork.Score);
        withMember.Evidence.ShouldContain("NDR");
    }

    [Fact]
    public void Ordering_is_deterministic_for_equally_scored_candidates()
    {
        // An unattended grab takes the first candidate, so the ordering must not depend on input order.
        var series = Tvdb(255986, "extra 3", "NDR");
        var a = Ours("zdf:a", "extra 3");
        var b = Ours("zdf:b", "extra 3");

        ShowMatcher.Rank(series, [a, b]).Select(c => c.Show.Id)
            .ShouldBe(ShowMatcher.Rank(series, [b, a]).Select(c => c.Show.Id));
    }

    [Fact]
    public void A_series_with_no_usable_name_matches_nothing()
    {
        ShowMatcher.Rank(Tvdb(1, "   "), [Ours("zdf:x", "heute-show")]).ShouldBeEmpty();
    }
}

public class EpisodeCorroborationTests
{
    // Distinct per-episode titles by default so date-matching cases are not accidentally decided by title.
    private static Episode Ep(string id, string date, string? title = null) => new()
    {
        Id = id,
        Title = title ?? $"untitled {id}",
        ShowId = "kika:die-biene-maja",
        BroadcastDate = new DateTimeOffset(DateTime.Parse(date), TimeSpan.Zero),
        Duration = TimeSpan.FromMinutes(25),
    };

    private static TvdbEpisode Tv(int season, int number, string? date, string? name = null) =>
        new(season, number, date is null ? null : DateOnly.Parse(date), name ?? $"tvdb S{season}E{number}");

    [Fact]
    public void The_wrong_vintage_of_the_same_named_series_is_rejected()
    {
        // The case that motivates this whole class. Both TVDB series are called "Die Biene Maja", so no
        // name comparison can separate them — but our 2013-2017 catalog has zero overlap with 1975.
        var ours = new[] { Ep("e1", "2013-04-28"), Ep("e2", "2013-05-05"), Ep("e3", "2017-11-28") };
        var tvdb1975 = new[] { Tv(1, 1, "1975-09-09"), Tv(1, 2, "1975-09-16"), Tv(1, 3, "1975-09-23") };

        var result = EpisodeCorroboration.Check(ours, tvdb1975);

        result.Matched.ShouldBe(0);
        result.IsCorroborated.ShouldBeFalse();
        result.Numbering.ShouldBeEmpty();
    }

    [Fact]
    public void The_right_vintage_is_corroborated_and_numbered()
    {
        var ours = new[] { Ep("e1", "2013-04-28"), Ep("e2", "2013-05-05") };
        var tvdb2013 = new[] { Tv(1, 1, "2013-04-28"), Tv(1, 2, "2013-05-05"), Tv(1, 3, "2013-05-12") };

        var result = EpisodeCorroboration.Check(ours, tvdb2013);

        result.IsCorroborated.ShouldBeTrue();
        result.Matched.ShouldBe(2);
        result.Numbering.Select(n => (n.EpisodeId, n.Season, n.Number))
            .ShouldBe([("e1", 1, 1), ("e2", 1, 2)]);
    }

    [Fact]
    public void Produces_the_year_season_numbering_sonarr_expects_for_heute_show()
    {
        // Measured from the real instance: TVDB models heute-show with year-seasons, and S2026E17 carries
        // airDate 2026-06-05 — exactly the BroadcastDate we crawled.
        var result = EpisodeCorroboration.Check(
            [Ep("zdf:heute-show:1", "2026-06-05"), Ep("zdf:heute-show:2", "2026-05-29")],
            [Tv(2026, 17, "2026-06-05"), Tv(2026, 16, "2026-05-29"), Tv(2026, 18, "2026-09-04")]);

        result.IsCorroborated.ShouldBeTrue();
        var first = result.Numbering.First(n => n.EpisodeId == "zdf:heute-show:1");
        (first.Season, first.Number).ShouldBe((2026, 17));
    }

    [Fact]
    public void A_one_day_slot_drift_still_matches()
    {
        // Broadcast slots straddle midnight and TVDB records the nominal date, so exact equality would
        // discard real matches.
        var result = EpisodeCorroboration.Check(
            [Ep("e1", "2026-06-06"), Ep("e2", "2026-05-28")],
            [Tv(2026, 17, "2026-06-05"), Tv(2026, 16, "2026-05-29")]);

        result.Matched.ShouldBe(2);
    }

    [Fact]
    public void An_exact_date_wins_over_one_within_tolerance()
    {
        // Both candidates are reachable; the same-day episode must be chosen regardless of list order.
        var result = EpisodeCorroboration.Check(
            [Ep("e1", "2026-06-05")],
            [Tv(2026, 16, "2026-06-04"), Tv(2026, 17, "2026-06-05")]);

        result.Numbering.ShouldHaveSingleItem().Number.ShouldBe(17);
    }

    [Fact]
    public void A_two_day_drift_is_too_far()
    {
        EpisodeCorroboration.Check([Ep("e1", "2026-06-07")], [Tv(2026, 17, "2026-06-05")])
            .Matched.ShouldBe(0);
    }

    [Fact]
    public void A_single_coincidental_date_in_a_large_catalog_does_not_corroborate()
    {
        // One hit out of ten is the shape of a coincidence, not of a real series match.
        var ours = Enumerable.Range(1, 10)
            .Select(i => Ep($"e{i}", $"2020-01-{i:D2}"))
            .ToList();

        var result = EpisodeCorroboration.Check(ours, [Tv(1, 1, "2020-01-01")]);

        result.Matched.ShouldBe(1);
        result.Ratio.ShouldBeLessThan(EpisodeCorroboration.MinimumRatio);
        result.IsCorroborated.ShouldBeFalse();
    }

    [Fact]
    public void A_lone_episode_matching_a_lone_episode_does_corroborate()
    {
        // Total agreement on a small catalog is the best evidence available and must not be discarded
        // merely for being small.
        EpisodeCorroboration.Check([Ep("e1", "2026-03-20")], [Tv(1, 1, "2026-03-20")])
            .IsCorroborated.ShouldBeTrue();
    }

    [Fact]
    public void Undated_tvdb_entries_are_ignored()
    {
        // TVDB carries unaired "TBA" rows with no date; matching against them would invent numbering.
        var result = EpisodeCorroboration.Check(
            [Ep("e1", "2026-06-05")],
            [Tv(2026, 18, null, null), Tv(2026, 19, null, null)]);

        result.Matched.ShouldBe(0);
        result.Numbering.ShouldBeEmpty();
        result.IsCorroborated.ShouldBeFalse();
        // Comparable still counts our episode: we had something to compare, TVDB just offered no key.
        result.Comparable.ShouldBe(1);
    }

    [Fact]
    public void An_empty_catalog_corroborates_nothing()
    {
        EpisodeCorroboration.Check([], [Tv(1, 1, "2020-01-01")]).IsCorroborated.ShouldBeFalse();
    }

    [Fact]
    public void A_rerun_is_matched_by_title_even_though_the_dates_disagree()
    {
        // The real KiKA case that invalidated a date-only design: TVDB aired "Knacks im Schneckenhaus" on
        // 2013-04-03, KiKA re-ran it on 2013-04-28. Date matching finds nothing; the title is unmistakable.
        var result = EpisodeCorroboration.Check(
            [Ep("kika:1", "2013-04-28", "Knacks im Schneckenhaus (S01/E08)"),
             Ep("kika:2", "2013-05-05", "Der Schmetterlingsball (S01/E09)")],
            [Tv(1, 8, "2013-04-03", "Knacks im Schneckenhaus"),
             Tv(1, 9, "2013-04-04", "Der Schmetterlingsball")]);

        result.IsCorroborated.ShouldBeTrue();
        result.Matched.ShouldBe(2);
        result.Numbering.ShouldAllBe(n => n.MatchedBy == MatchedBy.Title);
    }

    [Fact]
    public void Tvdb_numbering_wins_over_the_broadcasters_own()
    {
        // Measured: our S01/E27 is TVDB's S2E1. Emitting the broadcaster's number would file the episode
        // under the wrong season, and Sonarr matches against TVDB.
        var result = EpisodeCorroboration.Check(
            [Ep("kika:27", "2013-11-03", "Die falsche Wespe (S01/E27)")],
            [Tv(2, 1, "2013-10-01", "Die falsche Wespe")]);

        var only = result.Numbering.ShouldHaveSingleItem();
        (only.Season, only.Number).ShouldBe((2, 1));
    }

    [Fact]
    public void A_title_match_outranks_a_date_match_elsewhere_in_the_list()
    {
        // e1's title identifies S1E8 outright. e2 merely shares S1E8's air date. The title claim must win,
        // and e2 must not steal the episode by appearing to match on the weaker signal.
        var result = EpisodeCorroboration.Check(
            [Ep("e2", "2013-04-03", "something else entirely"),
             Ep("e1", "2013-04-28", "Knacks im Schneckenhaus (S01/E08)")],
            [Tv(1, 8, "2013-04-03", "Knacks im Schneckenhaus")]);

        var only = result.Numbering.ShouldHaveSingleItem();
        only.EpisodeId.ShouldBe("e1");
        only.MatchedBy.ShouldBe(MatchedBy.Title);
    }

    [Fact]
    public void Dated_topical_episodes_match_on_title_when_both_sides_carry_the_date()
    {
        // heute-show's TVDB episode name is literally "heute-show vom 5. Juni 2026" — the same string the
        // Mediathek uses. This is why the episode-title normaliser must not strip a trailing year.
        var result = EpisodeCorroboration.Check(
            [Ep("zdf:1", "2026-06-05", "heute-show vom 5. Juni 2026")],
            [Tv(2026, 17, "2026-06-05", "heute-show vom 5. Juni 2026")]);

        var only = result.Numbering.ShouldHaveSingleItem();
        (only.Season, only.Number).ShouldBe((2026, 17));
        only.MatchedBy.ShouldBe(MatchedBy.Title);
    }

    [Fact]
    public void Episodes_with_no_title_still_match_on_date()
    {
        var result = EpisodeCorroboration.Check(
            [Ep("e1", "2026-06-05", "  "), Ep("e2", "2026-05-29", "")],
            [Tv(2026, 17, "2026-06-05", null), Tv(2026, 16, "2026-05-29", "")]);

        result.Matched.ShouldBe(2);
        result.Numbering.ShouldAllBe(n => n.MatchedBy == MatchedBy.AirDate);
    }

    [Fact]
    public void One_tvdb_episode_is_never_claimed_by_two_of_ours()
    {
        // Adjacent-day assets both fall within tolerance of the same TVDB episode. Numbering both would
        // emit two releases claiming SxxEyy for one episode, which Sonarr would treat as duplicates.
        var result = EpisodeCorroboration.Check(
            [Ep("e1", "2026-06-05"), Ep("e2", "2026-06-06")],
            [Tv(2026, 17, "2026-06-05")]);

        result.Matched.ShouldBe(1);
        result.Numbering.ShouldHaveSingleItem().EpisodeId.ShouldBe("e1");   // the exact match wins
    }

    [Fact]
    public void An_exact_match_is_not_stolen_by_an_earlier_neighbour()
    {
        // e1 (06-04) is within tolerance of S17 (06-05), and comes first. If tolerance were resolved
        // per-episode in order, e1 would take S17 and e2 — whose date matches it exactly — would get
        // nothing. Assigning all exact matches first is what prevents that.
        var result = EpisodeCorroboration.Check(
            [Ep("e1", "2026-06-04"), Ep("e2", "2026-06-05")],
            [Tv(2026, 17, "2026-06-05")]);

        result.Numbering.ShouldHaveSingleItem().EpisodeId.ShouldBe("e2");
    }
}
