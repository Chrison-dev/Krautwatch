using Krautwatch.Application.Indexing;
using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Enums;
using Krautwatch.Domain.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Krautwatch.Application.Tests;

/// <summary>
/// Covers query-driven search (#58). The behaviour that matters most is that a crawl **outlives the request
/// that triggered it** — if the request's CancellationToken ever reached the crawl, every resolution would
/// be cancelled the instant the response was written and resolution would silently never work.
/// </summary>
public class OnDemandResolutionTests
{
    // ── harness ───────────────────────────────────────────────

    private sealed class Harness : IDisposable
    {
        public readonly FakeCrawler Crawler = new();
        public readonly IResolvedQueryRepository ResolvedQueries = Substitute.For<IResolvedQueryRepository>();
        public readonly IEpisodeRepository Episodes = Substitute.For<IEpisodeRepository>();
        public readonly ISettingsRepository Settings = Substitute.For<ISettingsRepository>();
        public readonly OnDemandResolutionOptions Options;
        public readonly OnDemandResolver Resolver;
        private readonly OnDemandResolutionService _service;
        private readonly IServiceProvider _provider;
        private readonly CancellationTokenSource _hostLifetime = new();

        public Harness(
            OnDemandResolutionOptions? options = null,
            SearchWaitMode waitMode = SearchWaitMode.ReturnFast,
            int waitSeconds = 1)
        {
            Options = options ?? new OnDemandResolutionOptions { CrawlTimeout = TimeSpan.FromSeconds(5) };

            Settings.GetAsync(Arg.Any<CancellationToken>()).Returns(new AppSettings
            {
                SearchWaitMode = waitMode,
                SearchWaitSeconds = waitSeconds,
            });

            var services = new ServiceCollection();
            services.AddSingleton(ResolvedQueries);
            services.AddSingleton(Episodes);
            services.AddSingleton(Settings);
            services.AddSingleton<IBroadcasterCrawler>(Crawler);
            _provider = services.BuildServiceProvider();

            var scopeFactory = _provider.GetRequiredService<IServiceScopeFactory>();
            Resolver = new OnDemandResolver(scopeFactory, Options, NullLogger<OnDemandResolver>.Instance);
            _service = new OnDemandResolutionService(
                Resolver, scopeFactory, Options, NullLogger<OnDemandResolutionService>.Instance);
        }

        public Task StartAsync() => _service.StartAsync(_hostLifetime.Token);

        public void Dispose()
        {
            _hostLifetime.Cancel();
            (_provider as IDisposable)?.Dispose();
            _hostLifetime.Dispose();
        }
    }

    private sealed class FakeCrawler : IBroadcasterCrawler
    {
        public string ProviderKey => "ard";
        public int Calls;
        public TaskCompletionSource? Gate;
        public Exception? Throw;
        public IReadOnlyList<Episode> Result = [];

        public async Task<IReadOnlyList<Episode>> CrawlShowAsync(string showQuery, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            if (Gate is not null)
                await Gate.Task.WaitAsync(ct);
            if (Throw is not null)
                throw Throw;
            return Result;
        }
    }

    private static Episode Ep(string id = "ard:1") => new()
    {
        Id = id, Title = "ep", ShowId = "ard:show",
        BroadcastDate = DateTimeOffset.UtcNow, Duration = TimeSpan.FromMinutes(30),
    };

    private static ResolvedQuery Previous(int resultCount, TimeSpan age) => new()
    {
        Query = "tatort", ResultCount = resultCount, LastAttemptedAt = DateTimeOffset.UtcNow - age,
    };

    // ── the cache ─────────────────────────────────────────────

    [Fact]
    public async Task A_fresh_successful_resolution_is_not_repeated()
    {
        using var h = new Harness();
        await h.StartAsync();
        h.ResolvedQueries.GetAsync("tatort", Arg.Any<CancellationToken>())
            .Returns(Previous(resultCount: 5, age: TimeSpan.FromMinutes(10)));

        var resolved = await h.Resolver.EnsureResolvedAsync("Tatort", TestContext.Current.CancellationToken);

        resolved.ShouldBeFalse();
        h.Crawler.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_fresh_empty_resolution_is_not_repeated()
    {
        // The important half: Sonarr re-issues the same failing query every RSS-Sync cycle, so without
        // negative caching each cycle would trigger a fresh multi-hop crawl of ARD.
        using var h = new Harness();
        await h.StartAsync();
        h.ResolvedQueries.GetAsync("tatort", Arg.Any<CancellationToken>())
            .Returns(Previous(resultCount: 0, age: TimeSpan.FromMinutes(5)));

        await h.Resolver.EnsureResolvedAsync("Tatort", TestContext.Current.CancellationToken);

        h.Crawler.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_stale_empty_resolution_is_retried()
    {
        using var h = new Harness();
        await h.StartAsync();
        h.ResolvedQueries.GetAsync("tatort", Arg.Any<CancellationToken>())
            .Returns(Previous(resultCount: 0, age: TimeSpan.FromHours(2))); // past the 45m negative TTL
        h.Crawler.Result = [Ep()];

        var resolved = await h.Resolver.EnsureResolvedAsync("Tatort", TestContext.Current.CancellationToken);

        resolved.ShouldBeTrue();
        h.Crawler.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task A_stale_successful_resolution_is_retried()
    {
        using var h = new Harness();
        await h.StartAsync();
        h.ResolvedQueries.GetAsync("tatort", Arg.Any<CancellationToken>())
            .Returns(Previous(resultCount: 3, age: TimeSpan.FromHours(12))); // past the 6h positive TTL

        await h.Resolver.EnsureResolvedAsync("Tatort", TestContext.Current.CancellationToken);

        h.Crawler.Calls.ShouldBe(1);
    }

    [Fact]
    public void Query_normalisation_collapses_case_and_whitespace()
    {
        ResolvedQuery.Normalise("  Die   Biene   MAJA ").ShouldBe("die biene maja");
    }

    // ── resolution behaviour ──────────────────────────────────

    [Fact]
    public async Task Persists_what_the_crawlers_return_and_records_the_attempt()
    {
        using var h = new Harness();
        await h.StartAsync();
        h.Crawler.Result = [Ep("ard:1"), Ep("ard:2")];

        var resolved = await h.Resolver.EnsureResolvedAsync("Tatort", TestContext.Current.CancellationToken);

        resolved.ShouldBeTrue();
        await h.Episodes.Received(1).UpsertManyAsync(
            Arg.Is<IEnumerable<Episode>>(e => e != null && e.Count() == 2), Arg.Any<CancellationToken>());
        await h.ResolvedQueries.Received(1).RecordAsync(
            Arg.Is<ResolvedQuery>(q => q != null && q.Query == "tatort" && q.ResultCount == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_empty_crawl_is_recorded_as_a_miss_not_skipped()
    {
        using var h = new Harness();
        await h.StartAsync();
        h.Crawler.Result = [];

        await h.Resolver.EnsureResolvedAsync("Nonexistent", TestContext.Current.CancellationToken);

        await h.ResolvedQueries.Received(1).RecordAsync(
            Arg.Is<ResolvedQuery>(q => q != null && q.ResultCount == 0), Arg.Any<CancellationToken>());
        await h.Episodes.DidNotReceive().UpsertManyAsync(
            Arg.Any<IEnumerable<Episode>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_throwing_crawler_does_not_break_the_resolution()
    {
        using var h = new Harness();
        await h.StartAsync();
        h.Crawler.Throw = new HttpRequestException("ard is down");

        var resolved = await h.Resolver.EnsureResolvedAsync("Tatort", TestContext.Current.CancellationToken);

        // Still completes and still records the attempt, so one broken broadcaster cannot wedge search.
        resolved.ShouldBeTrue();
        await h.ResolvedQueries.Received(1).RecordAsync(
            Arg.Any<ResolvedQuery>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Concurrent_identical_queries_crawl_once()
    {
        using var h = new Harness();
        await h.StartAsync();
        h.Crawler.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Crawler.Result = [Ep()];

        var first = h.Resolver.EnsureResolvedAsync("Tatort", TestContext.Current.CancellationToken);
        var second = h.Resolver.EnsureResolvedAsync("tatort", TestContext.Current.CancellationToken);

        h.Crawler.Gate.SetResult();
        await Task.WhenAll(first, second);

        h.Crawler.Calls.ShouldBe(1); // one crawl serving both callers
    }

    // ── the deadline, and the crawl outliving it ──────────────

    [Fact]
    public async Task The_request_deadline_releases_the_caller_while_the_crawl_continues()
    {
        using var h = new Harness(
            new OnDemandResolutionOptions { CrawlTimeout = TimeSpan.FromSeconds(10) },
            SearchWaitMode.ReturnFast, waitSeconds: 1);
        await h.StartAsync();
        h.Crawler.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Crawler.Result = [Ep()];

        // The crawl is held open, so the wait must expire first.
        var resolved = await h.Resolver.EnsureResolvedAsync("Tatort", TestContext.Current.CancellationToken);
        resolved.ShouldBeFalse();                       // released without a completed resolution
        h.Crawler.Calls.ShouldBe(1);                    // ...but the crawl did start

        // THE KEY ASSERTION: the request is long gone, yet letting the crawl finish still persists its
        // episodes. If the request token reached the crawl, this upsert would never happen.
        h.Crawler.Gate.SetResult();
        await WaitUntilAsync(() => h.Episodes.ReceivedCalls().Any(
            c => c.GetMethodInfo().Name == nameof(IEpisodeRepository.UpsertManyAsync)));

        await h.Episodes.Received(1).UpsertManyAsync(
            Arg.Any<IEnumerable<Episode>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cancelling_the_request_does_not_cancel_the_crawl()
    {
        // Stronger than the deadline case: here the caller's token is actively cancelled, as it would be if
        // Sonarr dropped the connection. The crawl must still finish and persist, because it runs under the
        // host lifetime. If the request token were ever threaded into the crawl, this would upsert nothing.
        using var h = new Harness(
            new OnDemandResolutionOptions { CrawlTimeout = TimeSpan.FromSeconds(10) },
            SearchWaitMode.ReturnFast, waitSeconds: 1);
        await h.StartAsync();
        h.Crawler.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Crawler.Result = [Ep()];

        using var request = new CancellationTokenSource();
        await h.Resolver.EnsureResolvedAsync("Tatort", request.Token);
        await request.CancelAsync();   // the caller is gone

        h.Crawler.Gate.SetResult();
        await WaitUntilAsync(() => h.Episodes.ReceivedCalls().Any(
            c => c.GetMethodInfo().Name == nameof(IEpisodeRepository.UpsertManyAsync)));

        await h.Episodes.Received(1).UpsertManyAsync(
            Arg.Any<IEnumerable<Episode>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Disabling_resolution_turns_search_back_into_a_plain_catalog_read()
    {
        using var h = new Harness(new OnDemandResolutionOptions { Enabled = false });
        await h.StartAsync();

        var resolved = await h.Resolver.EnsureResolvedAsync("Tatort", TestContext.Current.CancellationToken);

        resolved.ShouldBeFalse();
        h.Crawler.Calls.ShouldBe(0);
        await h.ResolvedQueries.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WaitForComplete_waits_for_a_slow_crawl_instead_of_answering_early()
    {
        // The operator asked for a complete first answer, so a crawl slower than any ReturnFast wait must
        // still be waited out.
        using var h = new Harness(
            new OnDemandResolutionOptions { CrawlTimeout = TimeSpan.FromSeconds(10) },
            SearchWaitMode.WaitForComplete);
        await h.StartAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Crawler.Gate = gate;
        h.Crawler.Result = [Ep()];

        var search = h.Resolver.EnsureResolvedAsync("Tatort", TestContext.Current.CancellationToken);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        search.IsCompleted.ShouldBeFalse("WaitForComplete must not answer before the crawl finishes");

        gate.SetResult();
        (await search).ShouldBeTrue();
    }

    [Fact]
    public async Task WaitForComplete_is_still_bounded_by_the_crawl_timeout()
    {
        // The guarantee is "it returns", not "it returns false". Two things can end the wait — the crawl
        // hitting its timeout (which completes the resolution, emptily) or the ceiling releasing the
        // caller — and which one wins is a race. Asserting the bool made this flaky: it passed locally and
        // failed on CI. Assert the actual guarantee instead: a stuck crawl must not hang the request.
        using var h = new Harness(
            new OnDemandResolutionOptions { CrawlTimeout = TimeSpan.FromMilliseconds(300) },
            SearchWaitMode.WaitForComplete);
        await h.StartAsync();
        h.Crawler.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await h.Resolver.EnsureResolvedAsync("Tatort", TestContext.Current.CancellationToken);
        stopwatch.Stop();

        // Generous, because the point is "bounded", not "fast" — a hang would blow straight past this.
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task An_unreadable_preference_falls_back_rather_than_failing_the_search()
    {
        using var h = new Harness();
        await h.StartAsync();
        h.Settings.GetAsync(Arg.Any<CancellationToken>())
            .Returns<AppSettings>(_ => throw new InvalidOperationException("db down"));
        h.Crawler.Result = [Ep()];

        // Must not throw: a broken settings read costs a default wait, not a failed search.
        var resolved = await h.Resolver.EnsureResolvedAsync("Tatort", TestContext.Current.CancellationToken);

        resolved.ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_query_never_resolves(string query)
    {
        using var h = new Harness();
        await h.StartAsync();

        (await h.Resolver.EnsureResolvedAsync(query, TestContext.Current.CancellationToken)).ShouldBeFalse();
        h.Crawler.Calls.ShouldBe(0);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(50);
    }
}
