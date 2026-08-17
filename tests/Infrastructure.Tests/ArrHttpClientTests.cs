using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Enums;
using Krautwatch.Domain.Interfaces;
using Krautwatch.Infrastructure.Arr;
using Krautwatch.Infrastructure.Secrets;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Krautwatch.Infrastructure.Tests;

/// <summary>
/// Covers the failure classification, since a specific cause is the whole point of this client — and
/// against a stub rather than a live test, because there is no real Sonarr to point at.
/// </summary>
public class ArrHttpClientTests
{
    private static ArrInstance Instance(string baseUrl = "http://sonarr:8989") => new()
    {
        Name = "Sonarr", Kind = ArrKind.Sonarr, BaseUrl = baseUrl, ApiKey = "key-abc",
    };

    private static ArrHttpClient Client(StubHandler handler) =>
        new(new HttpClient(handler),
            new SecretResolver(NullLogger<SecretResolver>.Instance),
            NullLogger<ArrHttpClient>.Instance);

    // ── success ───────────────────────────────────────────────

    [Fact]
    public async Task Reports_the_app_name_and_version_on_success()
    {
        var handler = StubHandler.Json("""{"appName":"Sonarr","version":"4.0.10.2544"}""");

        var result = await Client(handler).TestConnectionAsync(Instance(), TestContext.Current.CancellationToken);

        result.Ok.ShouldBeTrue();
        result.Failure.ShouldBe(ArrConnectionFailure.None);
        result.Message.ShouldBe("Sonarr 4.0.10.2544");
    }

    [Fact]
    public async Task Sends_the_api_key_as_a_header()
    {
        // Header rather than a query parameter so the key never lands in a proxy access log.
        var handler = StubHandler.Json("""{"appName":"Sonarr","version":"4.0"}""");

        await Client(handler).TestConnectionAsync(Instance(), TestContext.Current.CancellationToken);

        handler.LastRequest!.Headers.GetValues("X-Api-Key").ShouldHaveSingleItem().ShouldBe("key-abc");
        handler.LastRequest.RequestUri!.Query.ShouldNotContain("key-abc");
    }

    [Fact]
    public async Task Preserves_a_reverse_proxy_subpath()
    {
        var handler = StubHandler.Json("""{"appName":"Sonarr","version":"4.0"}""");

        await Client(handler).TestConnectionAsync(
            Instance("https://media.example.com/sonarr"), TestContext.Current.CancellationToken);

        handler.LastRequest!.RequestUri!.AbsoluteUri
            .ShouldBe("https://media.example.com/sonarr/api/v3/system/status");
    }

    [Theory]
    [InlineData("http://sonarr:8989")]
    [InlineData("http://sonarr:8989/")]
    public async Task Handles_a_base_url_with_or_without_a_trailing_slash(string baseUrl)
    {
        var handler = StubHandler.Json("""{"appName":"Sonarr","version":"4.0"}""");

        await Client(handler).TestConnectionAsync(Instance(baseUrl), TestContext.Current.CancellationToken);

        handler.LastRequest!.RequestUri!.AbsoluteUri
            .ShouldBe("http://sonarr:8989/api/v3/system/status");
    }

    [Fact]
    public async Task Resolves_a_stored_reference_before_sending_it()
    {
        var handler = StubHandler.Json("""{"appName":"Sonarr","version":"4.0"}""");
        Environment.SetEnvironmentVariable("KRAUTWATCH_TEST_ARR_KEY", "resolved-key");
        try
        {
            var instance = Instance();
            instance.ApiKey = "env:KRAUTWATCH_TEST_ARR_KEY";

            await Client(handler).TestConnectionAsync(instance, TestContext.Current.CancellationToken);

            handler.LastRequest!.Headers.GetValues("X-Api-Key")
                .ShouldHaveSingleItem().ShouldBe("resolved-key");
        }
        finally
        {
            Environment.SetEnvironmentVariable("KRAUTWATCH_TEST_ARR_KEY", null);
        }
    }

    [Fact]
    public async Task An_unresolvable_reference_fails_loudly_without_calling_out()
    {
        // Sending an empty key would come back as a 401 the operator cannot explain, when the real fault
        // is an unset variable — so this is its own failure mode, and no request is made at all.
        var handler = StubHandler.Json("""{"appName":"Sonarr","version":"4.0"}""");
        var instance = Instance();
        instance.ApiKey = "env:KRAUTWATCH_TEST_ARR_KEY_NOT_SET";

        var result = await Client(handler)
            .TestConnectionAsync(instance, TestContext.Current.CancellationToken);

        result.Ok.ShouldBeFalse();
        result.Failure.ShouldBe(ArrConnectionFailure.SecretUnresolved);
        result.Message.ShouldContain("KRAUTWATCH_TEST_ARR_KEY_NOT_SET");
        handler.LastRequest.ShouldBeNull();
    }

    // ── classified failures ───────────────────────────────────

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_rejected_key_is_reported_as_unauthorized(HttpStatusCode status)
    {
        var result = await Client(StubHandler.Status(status))
            .TestConnectionAsync(Instance(), TestContext.Current.CancellationToken);

        result.Ok.ShouldBeFalse();
        result.Failure.ShouldBe(ArrConnectionFailure.Unauthorized);
        result.Message.ShouldContain("API key");
    }

    [Fact]
    public async Task A_404_points_at_the_base_path_rather_than_the_key()
    {
        // The usual cause is a reverse-proxy subpath left out of the base URL — very different fix to a
        // wrong API key, so it must not be conflated with one.
        var result = await Client(StubHandler.Status(HttpStatusCode.NotFound))
            .TestConnectionAsync(Instance(), TestContext.Current.CancellationToken);

        result.Failure.ShouldBe(ArrConnectionFailure.ApiNotFound);
        result.Message.ShouldContain("subpath");
    }

    [Fact]
    public async Task A_200_from_something_that_is_not_an_arr_is_detected()
    {
        // Typically the wrong port entirely, landing on some other web app that answers happily.
        var result = await Client(StubHandler.Json("""{"hello":"world"}"""))
            .TestConnectionAsync(Instance(), TestContext.Current.CancellationToken);

        result.Failure.ShouldBe(ArrConnectionFailure.NotAnArrInstance);
        result.Message.ShouldContain("right port");
    }

    [Fact]
    public async Task Non_json_on_a_200_is_detected()
    {
        var result = await Client(StubHandler.Text("<html>hi</html>"))
            .TestConnectionAsync(Instance(), TestContext.Current.CancellationToken);

        result.Failure.ShouldBe(ArrConnectionFailure.NotAnArrInstance);
    }

    [Fact]
    public async Task A_socket_failure_is_reported_as_unreachable()
    {
        var handler = StubHandler.Throws(new HttpRequestException(
            "connect failed", new SocketException((int)SocketError.ConnectionRefused)));

        var result = await Client(handler).TestConnectionAsync(Instance(), TestContext.Current.CancellationToken);

        result.Failure.ShouldBe(ArrConnectionFailure.Unreachable);
        result.Message.ShouldContain("sonarr");
    }

    [Fact]
    public async Task A_tls_failure_is_distinguished_from_unreachable()
    {
        // Buried in the exception chain, and common with self-signed certificates.
        var handler = StubHandler.Throws(new HttpRequestException(
            "ssl", new AuthenticationException("cert rejected")));

        var result = await Client(handler).TestConnectionAsync(
            Instance("https://sonarr:8989"), TestContext.Current.CancellationToken);

        result.Failure.ShouldBe(ArrConnectionFailure.TlsFailure);
        result.Message.ShouldContain("certificate");
    }

    [Fact]
    public async Task A_timeout_is_reported_as_unreachable()
    {
        // HttpClient surfaces its own timeout as a cancellation, not a TimeoutException.
        var handler = StubHandler.Throws(new TaskCanceledException("timed out"));

        var result = await Client(handler).TestConnectionAsync(Instance(), TestContext.Current.CancellationToken);

        result.Failure.ShouldBe(ArrConnectionFailure.Unreachable);
        result.Message.ShouldContain("Timed out");
    }

    [Fact]
    public async Task An_unexpected_status_is_not_silently_treated_as_success()
    {
        var result = await Client(StubHandler.Status(HttpStatusCode.BadGateway))
            .TestConnectionAsync(Instance(), TestContext.Current.CancellationToken);

        result.Ok.ShouldBeFalse();
        result.Failure.ShouldBe(ArrConnectionFailure.Unexpected);
        result.Message.ShouldContain("502");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("sonarr:8989")]          // no scheme
    [InlineData("ftp://sonarr:8989")]    // wrong scheme
    public async Task A_malformed_base_url_fails_without_a_request(string baseUrl)
    {
        var handler = StubHandler.Json("""{"appName":"Sonarr","version":"4.0"}""");

        var result = await Client(handler).TestConnectionAsync(
            Instance(baseUrl), TestContext.Current.CancellationToken);

        result.Ok.ShouldBeFalse();
        handler.LastRequest.ShouldBeNull(); // never left the process
    }

    [Fact]
    public async Task Caller_cancellation_propagates_rather_than_being_reported_as_a_failure()
    {
        // A cancelled page load is not an instance problem, and must not be cached as one.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var handler = StubHandler.Throws(new TaskCanceledException("cancelled"));

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await Client(handler).TestConnectionAsync(Instance(), cts.Token));
    }

    // ── monitored list (#6) ───────────────────────────────────

    [Fact]
    public async Task Reads_monitored_series_from_Sonarr_and_skips_the_rest()
    {
        var handler = StubHandler.Json(
            """
            [
              {"title":"Extra 3","tvdbId":12345,"monitored":true},
              {"title":"Unmonitored Show","tvdbId":6789,"monitored":false},
              {"title":"Die Sendung mit der Maus","tvdbId":222,"monitored":true}
            ]
            """);

        var monitored = await Client(handler).GetMonitoredAsync(
            Instance(), TestContext.Current.CancellationToken);

        // The unmonitored ones come back in the same response, and are exactly what we must not spend
        // broadcaster searches on.
        monitored.ShouldBe([
            new ArrMonitoredItem("Extra 3", 12345),
            new ArrMonitoredItem("Die Sendung mit der Maus", 222),
        ]);

        handler.LastRequest!.RequestUri!.AbsolutePath.ShouldBe("/api/v3/series");
        handler.LastRequest.Headers.GetValues("X-Api-Key").ShouldHaveSingleItem().ShouldBe("key-abc");
    }

    [Fact]
    public async Task Reads_monitored_movies_from_Radarr_which_carries_no_TVDB_id()
    {
        var handler = StubHandler.Json("""[{"title":"Ein Film","tmdbId":550,"monitored":true}]""");
        var radarr = new ArrInstance
        {
            Name = "Radarr", Kind = ArrKind.Radarr, BaseUrl = "http://radarr:7878", ApiKey = "key-abc",
        };

        var monitored = await Client(handler).GetMonitoredAsync(
            radarr, TestContext.Current.CancellationToken);

        handler.LastRequest!.RequestUri!.AbsolutePath.ShouldBe("/api/v3/movie");

        // Radarr keys on TMDB, so there is no id a ShowMapping could resolve — title matching or nothing.
        monitored.ShouldHaveSingleItem().ShouldBe(new ArrMonitoredItem("Ein Film", null));
    }

    [Fact]
    public async Task A_subpath_in_the_base_url_is_preserved()
    {
        var handler = StubHandler.Json("[]");

        await Client(handler).GetMonitoredAsync(
            Instance("https://host/sonarr"), TestContext.Current.CancellationToken);

        handler.LastRequest!.RequestUri!.AbsolutePath.ShouldBe("/sonarr/api/v3/series");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task An_instance_that_refuses_yields_nothing_rather_than_throwing(HttpStatusCode status)
    {
        // This feeds an optional pre-warm, so an instance being down or misconfigured has to cost a log
        // line — never a failed crawl cycle (DR-011).
        var monitored = await Client(StubHandler.Status(status)).GetMonitoredAsync(
            Instance(), TestContext.Current.CancellationToken);

        monitored.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unreachable_instance_yields_nothing_rather_than_throwing()
    {
        var handler = StubHandler.Throws(new HttpRequestException("no route to host"));

        var monitored = await Client(handler).GetMonitoredAsync(
            Instance(), TestContext.Current.CancellationToken);

        monitored.ShouldBeEmpty();
    }

    [Fact]
    public async Task Something_that_is_not_an_arr_yields_nothing_rather_than_throwing()
    {
        var handler = StubHandler.Text("<html>hello</html>");

        var monitored = await Client(handler).GetMonitoredAsync(
            Instance(), TestContext.Current.CancellationToken);

        monitored.ShouldBeEmpty();
    }

    // ── stub ──────────────────────────────────────────────────

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _respond;
        public HttpRequestMessage? LastRequest { get; private set; }

        private StubHandler(Func<HttpResponseMessage> respond) => _respond = respond;

        public static StubHandler Json(string body) => new(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });

        public static StubHandler Text(string body) => new(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/html"),
        });

        public static StubHandler Status(HttpStatusCode status) =>
            new(() => new HttpResponseMessage(status));

        public static StubHandler Throws(Exception ex) => new(() => throw ex);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_respond());
        }
    }
}
