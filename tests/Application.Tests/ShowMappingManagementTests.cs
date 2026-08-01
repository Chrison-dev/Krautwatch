using Krautwatch.Application.Settings;
using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Enums;
using Krautwatch.Domain.Interfaces;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Krautwatch.Application.Tests;

public class ShowMappingReadModelTests
{
    private readonly IShowMappingRepository _mappings = Substitute.For<IShowMappingRepository>();

    private static ShowMapping Row(int tvdbId, string showId, int picks,
        MappingProvenance provenance = MappingProvenance.Learned) => new()
    {
        TvdbId = tvdbId,
        ShowId = showId,
        PickCount = picks,
        Provenance = provenance,
        Show = new Show { Id = showId, Title = showId, ChannelId = "zdf" },
    };

    [Fact]
    public async Task An_id_claimed_by_two_shows_is_flagged_as_contested()
    {
        _mappings.GetAllAsync(Arg.Any<CancellationToken>()).Returns(
            [Row(255986, "ard:extra-3", 2), Row(255986, "zdf:extra-3", 1), Row(234791, "zdf:heute-show", 0)]);

        var result = await new GetShowMappingsHandler(_mappings).HandleAsync(TestContext.Current.CancellationToken);

        result.Where(m => m.TvdbId == 255986).ShouldAllBe(m => m.Contested);
        result.Single(m => m.TvdbId == 234791).Contested.ShouldBeFalse();
        // Contested rows come first: they are the only ones an operator can usefully act on.
        result[0].Contested.ShouldBeTrue();
    }

    [Fact]
    public async Task A_contested_row_reports_how_many_grabs_would_settle_it()
    {
        _mappings.GetAllAsync(Arg.Any<CancellationToken>()).Returns(
            [Row(255986, "ard:extra-3", 2), Row(255986, "zdf:extra-3", 0)]);

        var result = await new GetShowMappingsHandler(_mappings).HandleAsync(TestContext.Current.CancellationToken);

        result.Single(m => m.ShowId == "ard:extra-3").PicksUntilSettled
            .ShouldBe(ShowMapping.AutoSelectAfterPicks - 2);
    }

    [Fact]
    public async Task An_uncontested_or_confirmed_mapping_reports_no_countdown()
    {
        _mappings.GetAllAsync(Arg.Any<CancellationToken>()).Returns(
        [
            Row(234791, "zdf:heute-show", 0),
            Row(255986, "ard:extra-3", 1, MappingProvenance.OperatorConfirmed),
            Row(255986, "zdf:extra-3", 1),
        ]);

        var result = await new GetShowMappingsHandler(_mappings).HandleAsync(TestContext.Current.CancellationToken);

        result.Single(m => m.ShowId == "zdf:heute-show").PicksUntilSettled.ShouldBeNull();
        result.Single(m => m.ShowId == "ard:extra-3").PicksUntilSettled.ShouldBeNull();
    }
}

public class ConfirmShowMappingTests
{
    private readonly IShowMappingRepository _mappings = Substitute.For<IShowMappingRepository>();

    [Fact]
    public async Task Confirming_one_show_removes_the_competing_claims()
    {
        // Leaving rejected candidates behind would keep showing the id as an open question, and their pick
        // counts would keep climbing off searches the operator has already decided.
        _mappings.GetByTvdbIdAsync(255986, Arg.Any<CancellationToken>()).Returns(
        [
            new ShowMapping { TvdbId = 255986, ShowId = "ard:extra-3" },
            new ShowMapping { TvdbId = 255986, ShowId = "zdf:extra-3" },
        ]);
        _mappings.UpsertAsync(Arg.Any<ShowMapping>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<ShowMapping>(call.Arg<ShowMapping>()!));

        await new ConfirmShowMappingHandler(_mappings)
            .HandleAsync(255986, "ard:extra-3", TestContext.Current.CancellationToken);

        await _mappings.Received(1).DeleteAsync(255986, "zdf:extra-3", Arg.Any<CancellationToken>());
        await _mappings.DidNotReceive().DeleteAsync(255986, "ard:extra-3", Arg.Any<CancellationToken>());
        await _mappings.Received(1).UpsertAsync(
            Arg.Is<ShowMapping>(m => m != null
                                  && m.ShowId == "ard:extra-3"
                                  && m.Provenance == MappingProvenance.OperatorConfirmed),
            Arg.Any<CancellationToken>());
    }
}

public class ShowMappingImportTests
{
    private readonly IShowMappingRepository _mappings = Substitute.For<IShowMappingRepository>();
    private readonly IEpisodeRepository _episodes = Substitute.For<IEpisodeRepository>();

    private void HaveShows(params string[] showIds) =>
        _episodes.GetShowsAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(
            showIds.Select(id => (
                Show: new Show { Id = id, Title = id, ChannelId = "zdf" },
                EpisodeCount: 1,
                LatestBroadcast: (DateTimeOffset?)null)).ToList());

    private ImportShowMappingsHandler Handler()
    {
        _mappings.UpsertAsync(Arg.Any<ShowMapping>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<ShowMapping>(call.Arg<ShowMapping>()!));
        return new ImportShowMappingsHandler(_mappings, _episodes);
    }

    [Fact]
    public async Task Applies_mappings_for_shows_this_catalog_has()
    {
        HaveShows("zdf:heute-show");

        var result = await Handler().HandleAsync(
            [new ShowMappingExport(234791, "zdf:heute-show", "heute-show", "zdf", MappingProvenance.Auto, 3)],
            TestContext.Current.CancellationToken);

        result.Applied.ShouldBe(1);
        await _mappings.Received(1).UpsertAsync(
            Arg.Is<ShowMapping>(m => m != null && m.Provenance == MappingProvenance.Imported),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Skips_a_mapping_to_a_show_we_have_never_crawled()
    {
        // It could never match anything, and would sit in the UI looking like configuration that works.
        HaveShows("zdf:heute-show");

        var result = await Handler().HandleAsync(
            [new ShowMappingExport(1, "ard:not-crawled", "Some Show", "ard", MappingProvenance.Auto, 0)],
            TestContext.Current.CancellationToken);

        result.Applied.ShouldBe(0);
        result.Skipped.ShouldBe(1);
        result.Notes.ShouldContain(note => note.Contains("no such show"));
    }

    [Fact]
    public async Task Never_overwrites_a_mapping_the_operator_confirmed()
    {
        // The local decision was made against this catalog by this operator; a file from elsewhere is
        // weaker evidence than that.
        HaveShows("zdf:heute-show");
        _mappings.GetByShowIdAsync("zdf:heute-show", Arg.Any<CancellationToken>()).Returns(
            new ShowMapping
            {
                TvdbId = 999,
                ShowId = "zdf:heute-show",
                Provenance = MappingProvenance.OperatorConfirmed,
            });

        var result = await Handler().HandleAsync(
            [new ShowMappingExport(234791, "zdf:heute-show", "heute-show", "zdf", MappingProvenance.Auto, 0)],
            TestContext.Current.CancellationToken);

        result.Applied.ShouldBe(0);
        result.Notes.ShouldContain(note => note.Contains("kept your confirmed mapping"));
        await _mappings.DidNotReceive().UpsertAsync(Arg.Any<ShowMapping>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(0, "zdf:heute-show")]
    [InlineData(234791, "")]
    public async Task Rejects_malformed_entries(int tvdbId, string showId)
    {
        HaveShows("zdf:heute-show");

        var result = await Handler().HandleAsync(
            [new ShowMappingExport(tvdbId, showId, null, null, MappingProvenance.Auto, 0)],
            TestContext.Current.CancellationToken);

        result.Applied.ShouldBe(0);
        result.Skipped.ShouldBe(1);
    }
}

/// <summary>
/// Parses RundfunkArr's published <c>rulesets.json</c> shape. Sample rows are taken verbatim from the real
/// file (checked 2026-07-28) so a format change on their side shows up as a test failure here.
/// </summary>
public class RundfunkArrRulesetsTests
{
    private const string Sample = """
    [
      {
        "id": 1, "mediaId": 10, "topic": "heute-show", "priority": 0,
        "filters": "[]", "titleRegexRules": "[]", "episodeRegex": null, "seasonRegex": null,
        "matchingStrategy": "ItemTitleEqualsAirdate",
        "media": { "media_id": 10, "media_name": "heute-show", "media_type": "show", "media_tvdbId": 234791 }
      },
      {
        "id": 2, "mediaId": 11, "topic": "Das Traumschiff", "priority": 0,
        "filters": "[]", "titleRegexRules": "[]", "episodeRegex": null, "seasonRegex": null,
        "matchingStrategy": "ItemTitleExact",
        "media": { "media_id": 11, "media_name": "Das Traumschiff", "media_type": "show", "media_tvdbId": 133371 }
      },
      {
        "id": 3, "mediaId": 12, "topic": "Ein generischer Eintrag", "priority": 0,
        "filters": "[]", "titleRegexRules": "[]", "episodeRegex": null, "seasonRegex": null,
        "matchingStrategy": "ItemTitleExact",
        "media": { "media_id": 12, "media_name": "generic", "media_type": "show", "media_tvdbId": null }
      }
    ]
    """;

    [Fact]
    public void Reads_topic_and_tvdb_id_pairs()
    {
        var hints = RundfunkArrRulesets.Parse(Sample);

        hints.Count.ShouldBe(2);
        hints.ShouldContain(h => h.TvdbId == 234791 && h.Topic == "heute-show");
        hints.ShouldContain(h => h.TvdbId == 133371 && h.Topic == "Das Traumschiff");
        hints.ShouldAllBe(h => h.Source == RundfunkArrRulesets.SourceName);
    }

    [Fact]
    public void Entries_with_no_tvdb_id_are_dropped()
    {
        // Their schema allows a null media_tvdbId for generic rulesets; those map to nothing.
        RundfunkArrRulesets.Parse(Sample).ShouldNotContain(h => h.Topic == "Ein generischer Eintrag");
    }

    [Fact]
    public void Topics_are_normalised_for_comparison()
    {
        RundfunkArrRulesets.Parse(Sample).Single(h => h.TvdbId == 234791)
            .NormalizedTopic.ShouldBe("heute show");
    }

    [Fact]
    public void The_same_pair_listed_twice_collapses()
    {
        // One show legitimately carries several rulesets — different priorities and regexes for the same
        // identity — and the composite key would collide.
        const string duplicated = """
        [
          { "topic": "heute-show", "media": { "media_tvdbId": 234791 } },
          { "topic": "heute-show", "media": { "media_tvdbId": 234791 } }
        ]
        """;

        RundfunkArrRulesets.Parse(duplicated).ShouldHaveSingleItem();
    }

    [Fact]
    public void An_empty_array_is_valid_and_yields_nothing()
    {
        RundfunkArrRulesets.Parse("[]").ShouldBeEmpty();
    }

    [Fact]
    public void A_payload_that_is_not_their_format_is_rejected_loudly()
    {
        // The UI catches this and tells the operator; silently importing zero entries would look like
        // success.
        Should.Throw<System.Text.Json.JsonException>(() => RundfunkArrRulesets.Parse("{\"nope\":true}"));
    }
}
