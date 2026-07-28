using Krautwatch.Application.Indexing;
using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Enums;
using Krautwatch.Domain.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Krautwatch.Application.Tests;

/// <summary>
/// Covers resolving a TVDB id backwards onto our catalog — the decision that produces a mapping, and the
/// deliberate refusal to make one on weak evidence.
/// </summary>
public class TvdbShowResolverTests
{
    private const int HeuteShow = 234791;

    private readonly IShowMappingRepository _mappings = Substitute.For<IShowMappingRepository>();
    private readonly IEpisodeRepository _episodes = Substitute.For<IEpisodeRepository>();
    private readonly ITvdbCatalog _tvdb = Substitute.For<ITvdbCatalog>();

    private TvdbShowResolver Resolver() =>
        new(_mappings, _episodes, _tvdb, NullLogger<TvdbShowResolver>.Instance);

    private static Show Show(string id, string title, string channel = "zdf") =>
        new() { Id = id, Title = title, ChannelId = channel, SeriesType = SeriesType.Daily };

    private static Episode Ep(string id, string showId, string date, string title) => new()
    {
        Id = id,
        Title = title,
        ShowId = showId,
        BroadcastDate = new DateTimeOffset(DateTime.Parse(date), TimeSpan.Zero),
        Duration = TimeSpan.FromMinutes(30),
    };

    private static TvdbEpisode Tv(int season, int number, string date, string name) =>
        new(season, number, DateOnly.Parse(date), name);

    private void HaveShows(params Show[] shows) =>
        _episodes.GetShowsAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(shows.Select(s => (Show: s, EpisodeCount: 1, LatestBroadcast: (DateTimeOffset?)null)).ToList());

    private void HaveEpisodes(string showId, params Episode[] eps) =>
        _episodes.GetByShowAsync(showId, Arg.Any<CancellationToken>()).Returns(eps);

    private void Configured(bool configured = true)
    {
        _tvdb.IsConfigured.Returns(configured);
        _mappings.GetByTvdbIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<ShowMapping>());
        _mappings.UpsertAsync(Arg.Any<ShowMapping>(), Arg.Any<CancellationToken>())
            // The substitute echoes back whatever was saved; Arg<T> is nullable-annotated, hence the !.
            .Returns(call => Task.FromResult<ShowMapping>(call.Arg<ShowMapping>()!));
    }

    [Fact]
    public async Task An_existing_mapping_is_used_without_re_deriving_it()
    {
        _tvdb.IsConfigured.Returns(true);
        _mappings.GetByTvdbIdAsync(HeuteShow, Arg.Any<CancellationToken>()).Returns(
            [new ShowMapping { TvdbId = HeuteShow, ShowId = "zdf:heute-show" }]);
        HaveEpisodes("zdf:heute-show", Ep("e1", "zdf:heute-show", "2026-06-05", "heute-show vom 5. Juni 2026"));
        _tvdb.GetEpisodesAsync(HeuteShow, Arg.Any<CancellationToken>())
            .Returns([Tv(2026, 17, "2026-06-05", "heute-show vom 5. Juni 2026")]);

        var result = await Resolver().ResolveAsync(HeuteShow, TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(ResolutionOutcome.AlreadyMapped);
        var only = result.Episodes.ShouldHaveSingleItem();
        (only.Season, only.Number).ShouldBe((2026, 17));

        // Re-deriving on every search would mean a TVDB series lookup per Sonarr poll.
        await _tvdb.DidNotReceive().GetSeriesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_single_corroborated_show_is_mapped_automatically()
    {
        Configured();
        _tvdb.GetSeriesAsync(HeuteShow, Arg.Any<CancellationToken>())
            .Returns(new TvdbSeries(HeuteShow, "heute-show", 2009, "ZDF", []));
        _tvdb.GetEpisodesAsync(HeuteShow, Arg.Any<CancellationToken>()).Returns(
            [Tv(2026, 17, "2026-06-05", "heute-show vom 5. Juni 2026"),
             Tv(2026, 16, "2026-05-29", "heute-show vom 29. Mai 2026")]);
        HaveShows(Show("zdf:heute-show", "heute-show"));
        HaveEpisodes("zdf:heute-show",
            Ep("e1", "zdf:heute-show", "2026-06-05", "heute-show vom 5. Juni 2026"),
            Ep("e2", "zdf:heute-show", "2026-05-29", "heute-show vom 29. Mai 2026"));

        var result = await Resolver().ResolveAsync(HeuteShow, TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(ResolutionOutcome.AutoMapped);
        result.Episodes.Count.ShouldBe(2);
        await _mappings.Received(1).UpsertAsync(
            Arg.Is<ShowMapping>(m => m != null
                                  && m.ShowId == "zdf:heute-show"
                                  && m.TvdbId == HeuteShow
                                  && m.Provenance == MappingProvenance.Auto),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Several_corroborated_shows_are_all_offered_and_none_is_persisted()
    {
        // The operator's design: do not guess, let the interactive search disambiguate and learn from the
        // grab. Persisting a guess here would defeat that, and a wrong id is worse than no id.
        Configured();
        _tvdb.GetSeriesAsync(255986, Arg.Any<CancellationToken>())
            .Returns(new TvdbSeries(255986, "Extra 3", 2006, "Norddeutscher Rundfunk (NDR)", []));
        _tvdb.GetEpisodesAsync(255986, Arg.Any<CancellationToken>()).Returns(
            [Tv(2026, 3, "2026-02-12", "extra 3 vom 12. Februar 2026"),
             Tv(2026, 4, "2026-02-19", "extra 3 vom 19. Februar 2026")]);

        HaveShows(Show("ard:extra-3", "extra 3", "ard"), Show("zdf:extra-3", "extra 3"));
        HaveEpisodes("ard:extra-3",
            Ep("a1", "ard:extra-3", "2026-02-12", "irrelevant"),
            Ep("a2", "ard:extra-3", "2026-02-19", "irrelevant"));
        HaveEpisodes("zdf:extra-3",
            Ep("z1", "zdf:extra-3", "2026-02-12", "irrelevant"),
            Ep("z2", "zdf:extra-3", "2026-02-19", "irrelevant"));

        var result = await Resolver().ResolveAsync(255986, TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(ResolutionOutcome.Candidates);
        result.Episodes.Select(e => e.Episode.ShowId).Distinct().Count().ShouldBe(2);
        await _mappings.DidNotReceive().UpsertAsync(Arg.Any<ShowMapping>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_name_match_with_no_episode_agreement_is_refused()
    {
        // The Biene Maja trap in its general form: the name matches but the episodes say otherwise. Mapping
        // on the name alone is how you end up filing 2013 episodes under a 1975 series.
        Configured();
        _tvdb.GetSeriesAsync(73518, Arg.Any<CancellationToken>())
            .Returns(new TvdbSeries(73518, "Maya the Bee", 1975, "ZDF", ["Die Biene Maja"]));
        _tvdb.GetEpisodesAsync(73518, Arg.Any<CancellationToken>())
            .Returns([Tv(1, 1, "1975-09-09", "Maja wird geboren")]);
        HaveShows(Show("kika:die-biene-maja", "Die Biene Maja", "kika"));
        HaveEpisodes("kika:die-biene-maja",
            Ep("k1", "kika:die-biene-maja", "2013-04-28", "Knacks im Schneckenhaus (S01/E08)"),
            Ep("k2", "kika:die-biene-maja", "2013-05-05", "Der Schmetterlingsball (S01/E09)"));

        var result = await Resolver().ResolveAsync(73518, TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(ResolutionOutcome.NoMatch);
        result.Episodes.ShouldBeEmpty();
        await _mappings.DidNotReceive().UpsertAsync(Arg.Any<ShowMapping>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_series_with_no_episode_list_is_never_mapped_on_name_alone()
    {
        Configured();
        _tvdb.GetSeriesAsync(HeuteShow, Arg.Any<CancellationToken>())
            .Returns(new TvdbSeries(HeuteShow, "heute-show", 2009, "ZDF", []));
        _tvdb.GetEpisodesAsync(HeuteShow, Arg.Any<CancellationToken>()).Returns([]);
        HaveShows(Show("zdf:heute-show", "heute-show"));

        var result = await Resolver().ResolveAsync(HeuteShow, TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(ResolutionOutcome.NoMatch);
        await _mappings.DidNotReceive().UpsertAsync(Arg.Any<ShowMapping>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unconfigured_tvdb_reports_unavailable_rather_than_no_match()
    {
        // The distinction matters: "we cannot tell" must not be recorded as "this show does not exist",
        // because the caller falls back to title search on Unavailable.
        _tvdb.IsConfigured.Returns(false);
        _mappings.GetByTvdbIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<ShowMapping>());

        var result = await Resolver().ResolveAsync(HeuteShow, TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(ResolutionOutcome.Unavailable);
        await _tvdb.DidNotReceive().GetSeriesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_mapped_show_still_publishes_when_tvdb_cannot_number_it()
    {
        // TVDB down but the mapping survives: emit the releases with the id and no numbers rather than
        // nothing. Sonarr can still identify the series, which beats an empty answer.
        _tvdb.IsConfigured.Returns(true);
        _mappings.GetByTvdbIdAsync(HeuteShow, Arg.Any<CancellationToken>()).Returns(
            [new ShowMapping { TvdbId = HeuteShow, ShowId = "zdf:heute-show" }]);
        _tvdb.GetEpisodesAsync(HeuteShow, Arg.Any<CancellationToken>()).Returns([]);
        HaveEpisodes("zdf:heute-show", Ep("e1", "zdf:heute-show", "2026-06-05", "heute-show"));

        var result = await Resolver().ResolveAsync(HeuteShow, TestContext.Current.CancellationToken);

        var only = result.Episodes.ShouldHaveSingleItem();
        only.TvdbId.ShouldBe(HeuteShow);
        only.Season.ShouldBeNull();
        only.Number.ShouldBeNull();
    }

    [Fact]
    public async Task Unnumbered_episodes_of_a_matched_show_are_still_published()
    {
        // A show can be correctly mapped while some assets fail to match an episode (a clip, or an entry
        // TVDB has not catalogued). Dropping them would silently shrink the catalog.
        Configured();
        _tvdb.GetSeriesAsync(HeuteShow, Arg.Any<CancellationToken>())
            .Returns(new TvdbSeries(HeuteShow, "heute-show", 2009, "ZDF", []));
        _tvdb.GetEpisodesAsync(HeuteShow, Arg.Any<CancellationToken>()).Returns(
            [Tv(2026, 17, "2026-06-05", "a"), Tv(2026, 16, "2026-05-29", "b")]);
        HaveShows(Show("zdf:heute-show", "heute-show"));
        HaveEpisodes("zdf:heute-show",
            Ep("e1", "zdf:heute-show", "2026-06-05", "a"),
            Ep("e2", "zdf:heute-show", "2026-05-29", "b"),
            Ep("e3", "zdf:heute-show", "2019-01-01", "an uncatalogued clip"));

        var result = await Resolver().ResolveAsync(HeuteShow, TestContext.Current.CancellationToken);

        result.Episodes.Count.ShouldBe(3);
        result.Episodes.Count(e => e.Season is not null).ShouldBe(2);
        result.Episodes.ShouldContain(e => e.Episode.Id == "e3" && e.Season == null);
    }
}
