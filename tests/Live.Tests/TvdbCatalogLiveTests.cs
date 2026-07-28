using Krautwatch.Application.Indexing;
using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Enums;
using Krautwatch.Domain.Interfaces;
using Krautwatch.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Krautwatch.Live.Tests;

/// <summary>
/// Exercises the real TheTVDB API through the production <c>ITvdbCatalog</c> adapter, then feeds the result
/// into the real matching pipeline. These are the tests that would have caught the two mistakes a synthetic
/// suite let through — that a date-only join rejects <i>Die Biene Maja</i>, and that KiKA's own episode
/// numbers disagree with TVDB's.
/// </summary>
/// <remarks>
/// Needs a TVDB API key, and skips cleanly without one so CI and contributors are unaffected:
/// <code>
///   dotnet user-secrets set "TvdbConfiguration:ApiKey" &lt;key&gt; --project src/Presentation/Web
///   KRAUTWATCH_TEST_TVDB_APIKEY=&lt;key&gt; ./build.sh TestLive
/// </code>
/// </remarks>
[Trait("Category", "Live")]
public class TvdbCatalogLiveTests
{
    private static readonly string? ApiKey =
        Environment.GetEnvironmentVariable("KRAUTWATCH_TEST_TVDB_APIKEY");

    private const int HeuteShow = 234791;
    private const int BieneMaja2013 = 266275;
    private const int BieneMaja1975 = 73518;
    private const int Tatort = 83214;

    private static ITvdbCatalog? Catalog()
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
            return null;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TvdbConfiguration:ApiKey"] = ApiKey,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTvdbCatalog(configuration);

        return services.BuildServiceProvider().GetRequiredService<ITvdbCatalog>();
    }

    private static Episode Ep(string id, string date, string title) => new()
    {
        Id = id,
        Title = title,
        ShowId = "test:show",
        BroadcastDate = new DateTimeOffset(DateTime.Parse(date), TimeSpan.Zero),
        Duration = TimeSpan.FromMinutes(25),
    };

    [Fact]
    public async Task Resolves_a_series_by_id_including_its_german_aliases()
    {
        if (Catalog() is not { } tvdb)
            return;

        var series = await tvdb.GetSeriesAsync(BieneMaja1975, TestContext.Current.CancellationToken);

        series.ShouldNotBeNull();
        // The primary name is Japanese/English; the German name we need lives in the aliases. This is the
        // whole reason matching reads AllNames rather than Name.
        series.AllNames.Select(TitleNormalizer.Normalize)
            .ShouldContain(TitleNormalizer.Normalize("Die Biene Maja"));
    }

    [Fact]
    public async Task Reads_the_episode_list_with_air_dates()
    {
        if (Catalog() is not { } tvdb)
            return;

        var episodes = await tvdb.GetEpisodesAsync(HeuteShow, TestContext.Current.CancellationToken);

        episodes.Count.ShouldBeGreaterThan(500);           // ~730 at time of writing
        episodes.ShouldContain(e => e.AirDate != null);

        // TVDB models heute-show with year-seasons, which is what makes S2026E17 the right answer for a
        // 2026-06-05 broadcast rather than some S1Exx.
        episodes.ShouldContain(e => e.Season >= 2020);
    }

    [Fact]
    public async Task Corroborates_the_right_biene_maja_and_rejects_the_wrong_one()
    {
        if (Catalog() is not { } tvdb)
            return;

        // Real KiKA titles and re-run broadcast dates: TVDB aired these on 2013-04-03/04.
        var ours = new[]
        {
            Ep("kika:1", "2013-04-28", "Knacks im Schneckenhaus (S01/E08)"),
            Ep("kika:2", "2013-05-05", "Der Schmetterlingsball (S01/E09)"),
            Ep("kika:3", "2013-05-12", "Max und die Vogelhochzeit (S01/E10)"),
        };

        var right = EpisodeCorroboration.Check(
            ours, await tvdb.GetEpisodesAsync(BieneMaja2013, TestContext.Current.CancellationToken));
        var wrong = EpisodeCorroboration.Check(
            ours, await tvdb.GetEpisodesAsync(BieneMaja1975, TestContext.Current.CancellationToken));

        right.IsCorroborated.ShouldBeTrue();
        right.Numbering.ShouldAllBe(n => n.MatchedBy == MatchedBy.Title);

        // The requirement that motivated all of this: both vintages are wanted, and the year must decide.
        wrong.IsCorroborated.ShouldBeFalse();
    }

    [Fact]
    public async Task Numbers_a_dated_topical_episode_the_way_sonarr_asks_for_it()
    {
        if (Catalog() is not { } tvdb)
            return;

        var result = EpisodeCorroboration.Check(
            [Ep("zdf:1", "2026-06-05", "heute-show vom 5. Juni 2026")],
            await tvdb.GetEpisodesAsync(HeuteShow, TestContext.Current.CancellationToken));

        // Sonarr's interactive search for this episode sends tvdbid=234791&season=2026&ep=17.
        var only = result.Numbering.ShouldHaveSingleItem();
        (only.Season, only.Number).ShouldBe((2026, 17));
    }

    [Fact]
    public async Task An_unrelated_german_series_is_not_corroborated()
    {
        if (Catalog() is not { } tvdb)
            return;

        // Negative control. Tatort is real, German, and prolific — exactly the kind of series a loose
        // matcher would happily attach heute-show episodes to.
        var result = EpisodeCorroboration.Check(
            [Ep("zdf:1", "2026-06-05", "heute-show vom 5. Juni 2026"),
             Ep("zdf:2", "2026-05-29", "heute-show vom 29. Mai 2026")],
            await tvdb.GetEpisodesAsync(Tatort, TestContext.Current.CancellationToken));

        result.IsCorroborated.ShouldBeFalse();
    }

    [Fact]
    public async Task Search_is_restricted_to_german_records()
    {
        if (Catalog() is not { } tvdb)
            return;

        // Unfiltered, TVDB returns the BBC's Panorama and three other foreign shows for this query. The
        // country filter is what makes title search usable at all.
        var results = await tvdb.SearchAsync("Panorama", TestContext.Current.CancellationToken);

        results.ShouldNotBeEmpty();
        results.ShouldNotContain(r => (r.Network ?? string.Empty).Contains("BBC", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_show_with_no_key_configured_degrades_instead_of_throwing()
    {
        // The most important behaviour for anyone without a TVDB account: matching gets worse, search keeps
        // working. Throwing here would surface as an indexer error, and Sonarr disables a failing indexer.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTvdbCatalog(new ConfigurationBuilder().Build());
        var tvdb = services.BuildServiceProvider().GetRequiredService<ITvdbCatalog>();

        tvdb.IsConfigured.ShouldBeFalse();
        (await tvdb.GetSeriesAsync(HeuteShow, TestContext.Current.CancellationToken)).ShouldBeNull();
        (await tvdb.GetEpisodesAsync(HeuteShow, TestContext.Current.CancellationToken)).ShouldBeEmpty();
        (await tvdb.SearchAsync("heute-show", TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }
}
