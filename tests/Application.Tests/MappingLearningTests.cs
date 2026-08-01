using Krautwatch.Application.Downloads;
using Krautwatch.Application.Indexing;
using Krautwatch.Domain.ValueObjects;
using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Enums;
using Krautwatch.Domain.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Krautwatch.Application.Tests;

public class ReleaseTokenTests
{
    [Fact]
    public void Round_trips_an_episode_id_and_a_tvdb_id()
    {
        var token = new ReleaseToken("ard:7tYDgyn04tGMb2oXIElK6L:-4440444432249674437", 255986).Encode();
        var parsed = ReleaseToken.Parse(token);

        parsed.EpisodeId.ShouldBe("ard:7tYDgyn04tGMb2oXIElK6L:-4440444432249674437");
        parsed.TvdbId.ShouldBe(255986);
    }

    [Theory]
    // Real broadcaster ids: provider-prefixed URL paths, CRIDs and base64 fragments. None may be mangled.
    [InlineData("zdf:content/documents/heute-show-vom-5-juni-2026-100.json")]
    [InlineData("kika:Y3JpZDovL3pkZi5kZS9QUk9EMS9TQ01TX3RpdmlfdmNtc192aWRlb18xODgwNzc4")]
    [InlineData("ard:7tYDgyn04tGMb2oXIElK6L:5600651129851421232")]
    public void Preserves_broadcaster_ids_exactly(string episodeId)
    {
        ReleaseToken.Parse(new ReleaseToken(episodeId, 234791).Encode()).EpisodeId.ShouldBe(episodeId);
        ReleaseToken.Parse(new ReleaseToken(episodeId, null).Encode()).EpisodeId.ShouldBe(episodeId);
    }

    [Fact]
    public void A_bare_episode_id_still_parses()
    {
        // Backward compatibility: NZBs emitted before this feature carry only the episode id, and may
        // already be sitting in a Sonarr queue.
        var parsed = ReleaseToken.Parse("zdf:heute-show:1");

        parsed.EpisodeId.ShouldBe("zdf:heute-show:1");
        parsed.TvdbId.ShouldBeNull();
    }

    [Theory]
    [InlineData("zdf:x|garbage")]
    [InlineData("zdf:x|tvdb=notanumber")]
    [InlineData("zdf:x|tvdb=0")]
    [InlineData("zdf:x|tvdb=-5")]
    public void An_unusable_suffix_is_ignored_rather_than_failing_the_download(string token)
    {
        // The episode id is what makes the download work. Rejecting the whole token over an unparseable
        // learning hint would trade a working download for a lost statistic.
        var parsed = ReleaseToken.Parse(token);

        parsed.TvdbId.ShouldBeNull();
        parsed.EpisodeId.ShouldBe(token);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Degenerate_input_parses_to_nothing(string? token)
    {
        var parsed = ReleaseToken.Parse(token);
        parsed.EpisodeId.ShouldBeEmpty();
        parsed.TvdbId.ShouldBeNull();
    }
}

public class GrabRecordsAPickTests
{
    private readonly IEpisodeRepository _episodes = Substitute.For<IEpisodeRepository>();
    private readonly IDownloadJobRepository _jobs = Substitute.For<IDownloadJobRepository>();
    private readonly IDownloadQueue _queue = Substitute.For<IDownloadQueue>();
    private readonly IShowMappingRepository _mappings = Substitute.For<IShowMappingRepository>();

    private AddDownloadByTokenHandler Handler() =>
        new(_episodes, _jobs, _queue, _mappings, NullLogger<AddDownloadByTokenHandler>.Instance);

    private void HaveEpisode(string id, string showId)
    {
        _episodes.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(new Episode
        {
            Id = id,
            Title = "an episode",
            ShowId = showId,
            BroadcastDate = DateTimeOffset.UtcNow,
            Duration = TimeSpan.FromMinutes(30),
            Streams = [new EpisodeStream { Url = "https://example.invalid/a.mp4", Quality = VideoQuality.High }],
        });
    }

    [Fact]
    public async Task A_grab_counts_as_a_pick_for_the_shows_tvdb_id()
    {
        HaveEpisode("ard:extra-3:1", "ard:extra-3");

        var token = new ReleaseToken("ard:extra-3:1", 255986).Encode();
        await Handler().HandleAsync(token, ct: TestContext.Current.CancellationToken);

        await _mappings.Received(1).RecordPickAsync(255986, "ard:extra-3", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_grab_without_a_tvdb_id_records_nothing()
    {
        HaveEpisode("ard:extra-3:1", "ard:extra-3");

        await Handler().HandleAsync("ard:extra-3:1", ct: TestContext.Current.CancellationToken);

        await _mappings.DidNotReceive()
            .RecordPickAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failure_to_learn_does_not_fail_the_download()
    {
        // Losing a vote costs one repetition. Losing the download the operator asked for is a visible bug.
        HaveEpisode("ard:extra-3:1", "ard:extra-3");
        _mappings.RecordPickAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<int>>(_ => throw new InvalidOperationException("database is down"));

        var jobId = await Handler().HandleAsync(
            new ReleaseToken("ard:extra-3:1", 255986).Encode(), ct: TestContext.Current.CancellationToken);

        jobId.ShouldNotBeNull();
        await _queue.Received(1).EnqueueAsync(jobId!.Value, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unknown_episode_is_still_rejected()
    {
        _episodes.GetByIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((Episode?)null);

        var jobId = await Handler().HandleAsync(
            new ReleaseToken("nope", 1).Encode(), ct: TestContext.Current.CancellationToken);

        jobId.ShouldBeNull();
    }
}

/// <summary>
/// Covers the decision the accumulated picks drive: keep asking, or answer on the operator's behalf.
/// </summary>
public class PickWeightedResolutionTests
{
    private const int Extra3 = 255986;

    private readonly IShowMappingRepository _mappings = Substitute.For<IShowMappingRepository>();
    private readonly IEpisodeRepository _episodes = Substitute.For<IEpisodeRepository>();
    private readonly ITvdbCatalog _tvdb = Substitute.For<ITvdbCatalog>();

    private TvdbShowResolver Resolver() =>
        new(_mappings, _episodes, _tvdb, NullLogger<TvdbShowResolver>.Instance);

    private static ShowMapping Mapping(string showId, int picks, MappingProvenance provenance = MappingProvenance.Learned) =>
        new() { TvdbId = Extra3, ShowId = showId, PickCount = picks, Provenance = provenance };

    private static Episode Ep(string id, string showId) => new()
    {
        Id = id,
        Title = "an episode",
        ShowId = showId,
        BroadcastDate = new DateTimeOffset(2026, 2, 12, 20, 0, 0, TimeSpan.Zero),
        Duration = TimeSpan.FromMinutes(30),
    };

    /// <summary>Mappings are returned in the repository's documented order: pinned first, then most-picked.</summary>
    private void Stored(params ShowMapping[] stored)
    {
        _tvdb.IsConfigured.Returns(true);
        _tvdb.GetEpisodesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        _mappings.GetByTvdbIdAsync(Extra3, Arg.Any<CancellationToken>()).Returns(
            stored.OrderByDescending(m => m.IsPinned).ThenByDescending(m => m.PickCount).ToList());

        foreach (var mapping in stored)
            _episodes.GetByShowAsync(mapping.ShowId, Arg.Any<CancellationToken>())
                .Returns([Ep($"{mapping.ShowId}:1", mapping.ShowId)]);
    }

    private static IEnumerable<string> Shows(ResolutionResult result) =>
        result.Episodes.Select(e => e.Episode.ShowId).Distinct();

    [Fact]
    public async Task Below_the_threshold_every_candidate_is_still_offered()
    {
        Stored(Mapping("ard:extra-3", picks: 4), Mapping("zdf:extra-3", picks: 1));

        var result = await Resolver().ResolveAsync(Extra3, TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(ResolutionOutcome.Candidates);
        Shows(result).ShouldBe(["ard:extra-3", "zdf:extra-3"]);   // most-picked first
    }

    [Fact]
    public async Task At_the_threshold_we_decide_on_the_operators_behalf()
    {
        // The operator's rule: five picks of the same show, with alternatives on offer every time, is a
        // decision. The sixth search should not ask again.
        Stored(Mapping("ard:extra-3", picks: TvdbShowResolver.AutoSelectAfterPicks),
               Mapping("zdf:extra-3", picks: 1));

        var result = await Resolver().ResolveAsync(Extra3, TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(ResolutionOutcome.Settled);
        Shows(result).ShouldBe(["ard:extra-3"]);
    }

    [Fact]
    public async Task A_tie_is_never_settled_however_high_the_count()
    {
        // Both shows grabbed equally often means the operator has been choosing both. Picking one would be
        // inventing an answer they never gave.
        Stored(Mapping("ard:extra-3", picks: 9), Mapping("zdf:extra-3", picks: 9));

        var result = await Resolver().ResolveAsync(Extra3, TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(ResolutionOutcome.Candidates);
        Shows(result).Count().ShouldBe(2);
    }

    [Fact]
    public async Task An_operator_override_wins_regardless_of_picks()
    {
        Stored(Mapping("zdf:extra-3", picks: 0, MappingProvenance.OperatorConfirmed),
               Mapping("ard:extra-3", picks: 50));

        var result = await Resolver().ResolveAsync(Extra3, TestContext.Current.CancellationToken);

        Shows(result).ShouldBe(["zdf:extra-3"]);
    }

    [Fact]
    public async Task A_lone_mapping_needs_no_picks_to_be_used()
    {
        // Nothing to disambiguate, so counting is irrelevant — this is the unambiguous auto-mapped case.
        Stored(Mapping("zdf:heute-show", picks: 0, MappingProvenance.Auto));

        var result = await Resolver().ResolveAsync(Extra3, TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(ResolutionOutcome.AlreadyMapped);
        Shows(result).ShouldBe(["zdf:heute-show"]);
    }

    [Fact]
    public async Task Candidates_are_persisted_so_the_first_pick_does_not_settle_the_question()
    {
        // Regression guard. If only the grabbed show were stored, the next search would see a single mapping
        // and treat it as settled — auto-selecting after one grab and discarding the counting entirely.
        _tvdb.IsConfigured.Returns(true);
        _mappings.GetByTvdbIdAsync(Extra3, Arg.Any<CancellationToken>()).Returns(new List<ShowMapping>());
        _mappings.UpsertAsync(Arg.Any<ShowMapping>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<ShowMapping>(call.Arg<ShowMapping>()!));
        _tvdb.GetSeriesAsync(Extra3, Arg.Any<CancellationToken>())
            .Returns(new TvdbSeries(Extra3, "Extra 3", 2006, "NDR", []));
        _tvdb.GetEpisodesAsync(Extra3, Arg.Any<CancellationToken>()).Returns(
            [new TvdbEpisode(2026, 3, new DateOnly(2026, 2, 12), "a"),
             new TvdbEpisode(2026, 4, new DateOnly(2026, 2, 19), "b")]);

        _episodes.GetShowsAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(
        [
            (new Show { Id = "ard:extra-3", Title = "extra 3", ChannelId = "ard" }, 2, (DateTimeOffset?)null),
            (new Show { Id = "zdf:extra-3", Title = "extra 3", ChannelId = "zdf" }, 2, (DateTimeOffset?)null),
        ]);
        foreach (var showId in new[] { "ard:extra-3", "zdf:extra-3" })
            _episodes.GetByShowAsync(showId, Arg.Any<CancellationToken>()).Returns(
                [Ep($"{showId}:1", showId), Ep2($"{showId}:2", showId)]);

        var result = await Resolver().ResolveAsync(Extra3, TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(ResolutionOutcome.Candidates);
        await _mappings.Received(2).UpsertAsync(
            Arg.Is<ShowMapping>(m => m != null
                                  && m.PickCount == 0
                                  && m.Provenance == MappingProvenance.Learned),
            Arg.Any<CancellationToken>());
    }

    private static Episode Ep2(string id, string showId) => new()
    {
        Id = id,
        Title = "another episode",
        ShowId = showId,
        BroadcastDate = new DateTimeOffset(2026, 2, 19, 20, 0, 0, TimeSpan.Zero),
        Duration = TimeSpan.FromMinutes(30),
    };
}
