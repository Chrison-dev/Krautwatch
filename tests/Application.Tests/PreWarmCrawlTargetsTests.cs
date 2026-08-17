using Krautwatch.Application.Crawling;
using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Enums;
using Krautwatch.Domain.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Krautwatch.Application.Tests;

/// <summary>
/// Pre-warming the standing crawl list from what Sonarr/Radarr monitor (#6). It is an optional
/// optimisation per DR-011, so the tests are as much about what it refuses to do — fail, fan out
/// beyond this host, or grow without bound — as about what it produces.
/// </summary>
public class PreWarmCrawlTargetsTests
{
    private readonly IArrInstanceRepository _instances = Substitute.For<IArrInstanceRepository>();
    private readonly IArrClient _arr = Substitute.For<IArrClient>();
    private readonly IShowMappingRepository _mappings = Substitute.For<IShowMappingRepository>();

    private PreWarmCrawlTargetsHandler Sut => new(_instances, _arr, _mappings,
        NullLogger<PreWarmCrawlTargetsHandler>.Instance);

    [Fact]
    public async Task A_mapped_series_becomes_one_precise_target_on_the_mapped_broadcaster()
    {
        GivenInstance();
        GivenMonitored(new ArrMonitoredItem("Extra 3", 12345));
        GivenMapping(12345, showId: "ard:extra-3", title: "Extra 3");

        var targets = await Sut.HandleAsync(["ard", "kika"], 50, TestContext.Current.CancellationToken);

        // The mapping names the broadcaster as well as the show, so there is nothing to guess at.
        targets.ShouldHaveSingleItem().ShouldBe(new CrawlTarget("ard", "Extra 3"));
    }

    [Fact]
    public async Task An_unmapped_series_is_tried_against_every_broadcaster_this_host_serves()
    {
        GivenInstance();
        GivenMonitored(new ArrMonitoredItem("Die Sendung mit der Maus", 999));
        _mappings.GetByTvdbIdAsync(999, Arg.Any<CancellationToken>()).Returns([]);

        var targets = await Sut.HandleAsync(["ard", "kika"], 50, TestContext.Current.CancellationToken);

        // A miss costs one search, and it is self-correcting: a grab creates the mapping, and the next
        // cycle collapses this to a single target.
        targets.ShouldBe([
            new CrawlTarget("ard", "Die Sendung mit der Maus"),
            new CrawlTarget("kika", "Die Sendung mit der Maus"),
        ]);
    }

    [Fact]
    public async Task A_show_mapped_to_a_broadcaster_this_host_does_not_serve_is_not_scheduled_here()
    {
        GivenInstance();
        GivenMonitored(new ArrMonitoredItem("heute-show", 777));
        GivenMapping(777, showId: "zdf:heute-show", title: "heute-show");

        var targets = await Sut.HandleAsync(["ard", "kika"], 50, TestContext.Current.CancellationToken);

        // The ZDF agent will schedule this one. Emitting it here would only produce a command that the
        // ARD agent drops with "no crawler registered".
        targets.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unreachable_instance_contributes_nothing_rather_than_failing_the_cycle()
    {
        GivenInstance();
        _arr.GetMonitoredAsync(Arg.Any<ArrInstance>(), Arg.Any<CancellationToken>()).Returns([]);

        var targets = await Sut.HandleAsync(["ard"], 50, TestContext.Current.CancellationToken);

        targets.ShouldBeEmpty();
    }

    [Fact]
    public async Task Mapped_targets_survive_the_cap_ahead_of_title_guesses()
    {
        GivenInstance();
        GivenMonitored(
            new ArrMonitoredItem("Guess A", null),
            new ArrMonitoredItem("Guess B", null),
            new ArrMonitoredItem("Extra 3", 12345));
        GivenMapping(12345, showId: "ard:extra-3", title: "Extra 3");

        var targets = await Sut.HandleAsync(["ard"], 2, TestContext.Current.CancellationToken);

        // When the cap bites, a known-good show must not be the thing that gets dropped.
        targets.Count.ShouldBe(2);
        targets.ShouldContain(new CrawlTarget("ard", "Extra 3"));
    }

    [Fact]
    public async Task The_same_show_monitored_on_two_instances_is_scheduled_once()
    {
        _instances.GetEnabledAsync(Arg.Any<CancellationToken>())
            .Returns([Instance("Sonarr 1"), Instance("Sonarr 2")]);
        _arr.GetMonitoredAsync(Arg.Any<ArrInstance>(), Arg.Any<CancellationToken>())
            .Returns([new ArrMonitoredItem("Extra 3", null)]);

        var targets = await Sut.HandleAsync(["ard"], 50, TestContext.Current.CancellationToken);

        targets.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Nothing_is_asked_of_the_instances_when_the_host_serves_no_broadcaster()
    {
        var targets = await Sut.HandleAsync([], 50, TestContext.Current.CancellationToken);

        targets.ShouldBeEmpty();
        await _instances.DidNotReceive().GetEnabledAsync(Arg.Any<CancellationToken>());
    }

    // ── arrange helpers ───────────────────────────────────────

    private void GivenInstance() =>
        _instances.GetEnabledAsync(Arg.Any<CancellationToken>()).Returns([Instance("Sonarr")]);

    private void GivenMonitored(params ArrMonitoredItem[] items) =>
        _arr.GetMonitoredAsync(Arg.Any<ArrInstance>(), Arg.Any<CancellationToken>()).Returns(items);

    private void GivenMapping(int tvdbId, string showId, string title) =>
        _mappings.GetByTvdbIdAsync(tvdbId, Arg.Any<CancellationToken>()).Returns([
            new ShowMapping
            {
                TvdbId = tvdbId,
                ShowId = showId,
                Show = new Show { Id = showId, Title = title, ChannelId = showId.Split(':')[0] },
            },
        ]);

    private static ArrInstance Instance(string name) => new()
    {
        Name = name,
        Kind = ArrKind.Sonarr,
        BaseUrl = "http://sonarr:8989",
        ApiKey = "key",
        Enabled = true,
    };
}
