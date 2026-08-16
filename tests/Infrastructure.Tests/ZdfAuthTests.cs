using System.Net;
using System.Text;
using Krautwatch.Infrastructure.Crawling.Zdf;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Shouldly;
using Xunit;

namespace Krautwatch.Infrastructure.Tests;

/// <summary>
/// ZDF authenticates with a static bearer that ZDF rotates (#13). These cover the three things that
/// have to hold when it happens: the key comes from configuration, a rejection is loud rather than
/// silent, and it shows up on /health.
/// </summary>
public class ZdfAuthTests
{
    [Fact]
    public async Task The_configured_key_is_what_gets_sent()
    {
        var handler = StubHandler.Json("{}");
        var client = new ZdfCatalogClient(new HttpClient(handler),
            new ZdfOptions { ApiAuthKey = "rotated-key-from-config" });

        await client.SearchEpisodesAsync("heute-show", TestContext.Current.CancellationToken);

        handler.LastRequest!.Headers.GetValues("Api-Auth")
            .ShouldHaveSingleItem()
            .ShouldBe("Bearer rotated-key-from-config");
    }

    [Fact]
    public async Task Without_configuration_the_shipped_default_still_works()
    {
        var handler = StubHandler.Json("{}");
        var client = new ZdfCatalogClient(new HttpClient(handler));

        await client.SearchEpisodesAsync("heute-show", TestContext.Current.CancellationToken);

        handler.LastRequest!.Headers.GetValues("Api-Auth")
            .ShouldHaveSingleItem()
            .ShouldBe($"Bearer {ZdfOptions.DefaultApiAuthKey}");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_rejected_key_throws_instead_of_looking_like_an_empty_catalog(HttpStatusCode status)
    {
        var handler = StubHandler.Status(status);
        var client = new ZdfCatalogClient(new HttpClient(handler));

        // The old behaviour returned null here, which the crawler reads as "no episodes" — a broken
        // indexer that looks like an empty one.
        var thrown = await Should.ThrowAsync<ZdfAuthRejectedException>(async () =>
            await client.SearchEpisodesAsync("heute-show", TestContext.Current.CancellationToken));

        thrown.StatusCode.ShouldBe(status);
        thrown.Message.ShouldContain("Zdf:ApiAuthKey");
    }

    [Fact]
    public async Task A_rejected_key_is_not_retried()
    {
        var handler = StubHandler.Status(HttpStatusCode.Unauthorized);
        var client = new ZdfCatalogClient(new HttpClient(handler));

        await Should.ThrowAsync<ZdfAuthRejectedException>(async () =>
            await client.SearchEpisodesAsync("heute-show", TestContext.Current.CancellationToken));

        // Transient failures get three attempts. An auth failure will answer identically every time,
        // so retrying only makes every crawl slower before it fails anyway.
        handler.Attempts.ShouldBe(1);
    }

    [Fact]
    public async Task A_transient_failure_is_still_retried_and_stays_silent()
    {
        var handler = StubHandler.Status(HttpStatusCode.InternalServerError);
        var client = new ZdfCatalogClient(new HttpClient(handler));

        var episodes = await client.SearchEpisodesAsync("heute-show", TestContext.Current.CancellationToken);

        episodes.ShouldBeEmpty();
        handler.Attempts.ShouldBe(3);
    }

    [Fact]
    public async Task Health_reports_degraded_after_a_rejection_and_recovers_on_success()
    {
        var state = new ZdfAuthState();
        var check = new ZdfAuthHealthCheck(state);
        var context = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("zdf-auth", check, HealthStatus.Degraded, null),
        };

        (await check.CheckHealthAsync(context, TestContext.Current.CancellationToken))
            .Status.ShouldBe(HealthStatus.Healthy);

        state.RecordRejection(HttpStatusCode.Unauthorized, DateTimeOffset.UtcNow);

        var degraded = await check.CheckHealthAsync(context, TestContext.Current.CancellationToken);

        // Degraded, not Unhealthy: /health answers 503 for Unhealthy, and that endpoint is what the
        // compose healthcheck probes — restarting the agent cannot fix a rotated key.
        degraded.Status.ShouldBe(HealthStatus.Degraded);
        degraded.Description.ShouldContain("rotated");

        state.RecordSuccess();

        (await check.CheckHealthAsync(context, TestContext.Current.CancellationToken))
            .Status.ShouldBe(HealthStatus.Healthy);
    }

    [Fact]
    public async Task The_client_records_the_rejection_where_health_can_see_it()
    {
        var state = new ZdfAuthState();
        var client = new ZdfCatalogClient(new HttpClient(StubHandler.Status(HttpStatusCode.Forbidden)),
            authState: state);

        await Should.ThrowAsync<ZdfAuthRejectedException>(async () =>
            await client.SearchEpisodesAsync("heute-show", TestContext.Current.CancellationToken));

        var snapshot = state.Snapshot();
        snapshot.IsRejected.ShouldBeTrue();
        snapshot.ConsecutiveRejections.ShouldBe(1);
        snapshot.LastStatusCode.ShouldBe(HttpStatusCode.Forbidden);
        snapshot.FirstRejectionAt.ShouldNotBeNull();
    }

    // ── stub ──────────────────────────────────────────────────

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _respond;

        public HttpRequestMessage? LastRequest { get; private set; }
        public int Attempts { get; private set; }

        private StubHandler(Func<HttpResponseMessage> respond) => _respond = respond;

        public static StubHandler Json(string body) => new(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });

        public static StubHandler Status(HttpStatusCode status) => new(() => new HttpResponseMessage(status));

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            Attempts++;
            return Task.FromResult(_respond());
        }
    }
}
