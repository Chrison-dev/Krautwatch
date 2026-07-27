using System.Net;
using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Interfaces;
using Krautwatch.Domain.Options;
using Krautwatch.Infrastructure.Downloads;
using Krautwatch.Infrastructure.Jobs;
using Krautwatch.Infrastructure.Persistence;
using Krautwatch.Infrastructure.Proxies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Krautwatch.Infrastructure.Tests;

[Collection(PostgresCollection.Name)]
public class ProxyRepositoryTests(PostgresFixture postgres) : IAsyncLifetime
{
    private DbContextOptions<AppDbContext> _options = null!;

    public async ValueTask InitializeAsync() => _options = await postgres.CreateDatabaseAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private ProxyRepository Repo() => new(new AppDbContext(_options));

    private static Proxy P(string host, int port, double upTime = 50, int speed = 5, bool? probeOk = null) => new()
    {
        Id = $"{host}:{port}", Host = host, Port = port, Protocol = "http", Source = "geonode",
        Country = "DE", UpTime = upTime, Speed = speed, LastProbeOk = probeOk,
    };

    [Fact]
    public async Task Upsert_adds_new_rows_then_refreshes_metrics_but_keeps_our_feedback()
    {
        await Repo().UpsertBatchAsync([P("1.1.1.1", 3128, upTime: 40)], TestContext.Current.CancellationToken);
        await Repo().RecordProbeResultAsync("http://1.1.1.1:3128", ok: true, TestContext.Current.CancellationToken);

        // A refresh brings new source metrics for the same host:port.
        await Repo().UpsertBatchAsync([P("1.1.1.1", 3128, upTime: 90)], TestContext.Current.CancellationToken);

        var row = (await Repo().GetRankedAsync("DE", 10, TestContext.Current.CancellationToken)).ShouldHaveSingleItem();
        row.UpTime.ShouldBe(90);            // source metric refreshed
        row.LastProbeOk.ShouldBe(true);     // our feedback preserved across the refresh
    }

    [Fact]
    public async Task GetRanked_puts_probed_good_first_then_orders_by_uptime()
    {
        await Repo().UpsertBatchAsync(
        [
            P("bad", 1, upTime: 99, probeOk: false),   // known-bad sinks despite high uptime
            P("unknown", 2, upTime: 60),               // untested
            P("good", 3, upTime: 10, probeOk: true),   // known-good floats up despite low uptime
        ], TestContext.Current.CancellationToken);

        var ranked = await Repo().GetRankedAsync("DE", 10, TestContext.Current.CancellationToken);

        ranked.Select(p => p.Host).ShouldBe(["good", "unknown", "bad"]);
    }

    [Fact]
    public async Task GetRanked_only_returns_the_requested_country()
    {
        var de = P("de", 1);
        var other = new Proxy { Id = "ch:2", Host = "ch", Port = 2, Protocol = "http", Source = "geonode", Country = "CH" };
        await Repo().UpsertBatchAsync([de, other], TestContext.Current.CancellationToken);

        (await Repo().GetRankedAsync("DE", 10, TestContext.Current.CancellationToken)).ShouldHaveSingleItem().Host.ShouldBe("de");
    }
}

public class GeoNodeProxyListSourceTests
{
    private const string Json =
        """
        { "data": [
          { "ip": "1.2.3.4", "port": "3128", "protocols": ["http"], "country": "DE",
            "upTime": 97.5, "speed": 14, "responseTime": 87, "latency": 6.2,
            "anonymityLevel": "elite", "lastChecked": 1785071922 },
          { "ip": "bad", "protocols": ["http"] }
        ] }
        """;

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }

    [Fact]
    public async Task Parses_the_geonode_shape_and_skips_malformed_rows()
    {
        var http = new HttpClient(new StubHandler(Json));
        var source = new GeoNodeProxyListSource(http, new ProxyListOptions(), NullLogger<GeoNodeProxyListSource>.Instance);

        var proxies = await source.FetchAsync(TestContext.Current.CancellationToken);

        var p = proxies.ShouldHaveSingleItem(); // the port-less row is dropped
        p.Id.ShouldBe("1.2.3.4:3128");
        p.Url.ShouldBe("http://1.2.3.4:3128");
        p.UpTime.ShouldBe(97.5);
        p.Speed.ShouldBe(14);
        p.AnonymityLevel.ShouldBe("elite");
        p.SourceLastChecked.ShouldBe(DateTimeOffset.FromUnixTimeSeconds(1785071922));
    }
}

public class EgressProxyProviderTests
{
    [Fact]
    public async Task Byo_proxy_is_offered_when_configured()
    {
        var opts = new EgressProxyOptions { ProxyUrl = "http://10.0.0.9:3128" };
        var sut = new EgressProxyProvider(opts, Substitute.For<IServiceScopeFactory>());

        (await sut.GetCandidatesAsync(TestContext.Current.CancellationToken)).ShouldBe(["http://10.0.0.9:3128"]);
    }

    [Fact]
    public async Task No_candidates_when_nothing_is_configured()
    {
        var sut = new EgressProxyProvider(new EgressProxyOptions(), Substitute.For<IServiceScopeFactory>());

        (await sut.GetCandidatesAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }
}

public class RawMp4GeoFailFastTests
{
    private sealed class NoEgress : IEgressProxyProvider
    {
        public Task<IReadOnlyList<string>> GetCandidatesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
        public Task ReportResultAsync(string proxyUrl, bool ok, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task A_geo_restricted_job_with_no_egress_fails_fast_without_touching_the_network()
    {
        var provider = new RawMp4DownloadProvider(new FileNamingService(), new NoEgress(), NullLogger<RawMp4DownloadProvider>.Instance);
        var job = new DownloadJob
        {
            EpisodeId = "kika:1", StreamUrl = "https://cdn/x.mp4", GeoRestricted = true,
            Episode = new Episode { Id = "kika:1", Title = "x", ShowId = "s", Duration = TimeSpan.Zero },
        };

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => provider.DownloadAsync(job, Path.GetTempPath(), new Progress<double>(), CancellationToken.None));
        ex.Message.ShouldContain("geo-restricted");
    }
}
