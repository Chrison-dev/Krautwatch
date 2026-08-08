using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace Krautwatch.Infrastructure.Arr;

/// <summary>
/// Talks to a Sonarr/Radarr instance over its v3 API. Both expose the same
/// <c>GET /api/v3/system/status</c> shape, authenticated with an <c>X-Api-Key</c> header.
/// </summary>
/// <remarks>
/// The point of this class is that failures are **classified**, not just reported. "It doesn't work" is
/// the most common self-hosting complaint and the operator can only act on a specific cause: a wrong port
/// looks nothing like a wrong API key, and a reverse-proxy subpath left off the base URL looks nothing
/// like either. See <see cref="ArrConnectionFailure"/>.
/// </remarks>
public class ArrHttpClient(HttpClient http, ISecretResolver secrets, ILogger<ArrHttpClient> logger)
    : IArrClient
{
    private const string StatusPath = "api/v3/system/status";

    public async Task<ArrConnectionResult> TestConnectionAsync(
        ArrInstance instance,
        CancellationToken ct = default)
    {
        if (!TryBuildStatusUri(instance.BaseUrl, out var uri))
            return ArrConnectionResult.Fail(
                ArrConnectionFailure.Unexpected,
                $"'{instance.BaseUrl}' is not a valid absolute URL.");

        // The stored key may be a pointer rather than the secret. Resolve here, at the point of use —
        // never on repository read, which would let an edit round-trip persist the resolved value.
        var apiKey = secrets.Resolve(instance.ApiKey);
        if (apiKey.Origin == SecretOrigin.Unresolved)
            return ArrConnectionResult.Fail(ArrConnectionFailure.SecretUnresolved, apiKey.Problem!);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            // The *arr apps accept the key as a header or a query parameter. Header, so it never lands
            // in a proxy access log.
            request.Headers.TryAddWithoutValidation("X-Api-Key", apiKey.Value);

            using var response = await http.SendAsync(request, ct);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return ArrConnectionResult.Fail(
                    ArrConnectionFailure.Unauthorized,
                    "Reached the instance, but the API key was rejected.");

            if (response.StatusCode == HttpStatusCode.NotFound)
                return ArrConnectionResult.Fail(
                    ArrConnectionFailure.ApiNotFound,
                    $"No API at {uri}. If it sits behind a reverse proxy on a subpath, include that "
                    + "path in the base URL.");

            if (!response.IsSuccessStatusCode)
                return ArrConnectionResult.Fail(
                    ArrConnectionFailure.Unexpected,
                    $"Unexpected response {(int)response.StatusCode} {response.ReasonPhrase}.");

            return await ReadStatusAsync(response, uri, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the caller gave up; not an instance failure
        }
        catch (OperationCanceledException)
        {
            // HttpClient surfaces its own timeout as a cancellation, not a TimeoutException.
            return ArrConnectionResult.Fail(
                ArrConnectionFailure.Unreachable,
                "Timed out. Check the host and port, and that the instance is running.");
        }
        catch (HttpRequestException ex)
        {
            return Classify(ex, uri);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unexpected failure testing {BaseUrl}.", instance.BaseUrl);
            return ArrConnectionResult.Fail(ArrConnectionFailure.Unexpected, ex.Message);
        }
    }

    private static async Task<ArrConnectionResult> ReadStatusAsync(
        HttpResponseMessage response,
        Uri uri,
        CancellationToken ct)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

            // A 200 from something that isn't an *arr — typically the wrong port entirely, landing on some
            // other web app that happily returns a page.
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("appName", out var appName)
                || !doc.RootElement.TryGetProperty("version", out var version))
            {
                return ArrConnectionResult.Fail(
                    ArrConnectionFailure.NotAnArrInstance,
                    $"{uri} answered, but not with a Sonarr/Radarr status. Is that the right port?");
            }

            return ArrConnectionResult.Success(
                appName.GetString() ?? "unknown",
                version.GetString() ?? "unknown");
        }
        catch (JsonException)
        {
            return ArrConnectionResult.Fail(
                ArrConnectionFailure.NotAnArrInstance,
                $"{uri} answered with something that isn't JSON. Is that the right port?");
        }
    }

    /// <summary>Maps transport-level failures onto causes an operator can act on.</summary>
    private static ArrConnectionResult Classify(HttpRequestException ex, Uri uri)
    {
        // TLS problems hide inside the exception chain, and are common with self-signed certificates.
        for (Exception? inner = ex; inner is not null; inner = inner.InnerException)
        {
            if (inner is AuthenticationException)
                return ArrConnectionResult.Fail(
                    ArrConnectionFailure.TlsFailure,
                    $"TLS handshake with {uri.Host} failed — often a self-signed or expired certificate.");

            if (inner is SocketException socket)
                return ArrConnectionResult.Fail(
                    ArrConnectionFailure.Unreachable,
                    $"Could not reach {uri.Host}:{uri.Port} ({socket.SocketErrorCode}).");
        }

        return ArrConnectionResult.Fail(ArrConnectionFailure.Unreachable, ex.Message);
    }

    /// <summary>
    /// Builds the status URL, preserving any subpath in the configured base URL — instances behind a
    /// reverse proxy are commonly served from something like <c>https://host/sonarr</c>.
    /// </summary>
    internal static bool TryBuildStatusUri(string baseUrl, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(baseUrl)
            || !Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            return false;

        // A trailing slash matters to Uri's relative resolution: without it the last path segment would be
        // replaced rather than appended, silently dropping the subpath.
        var basePath = parsed.AbsoluteUri.EndsWith('/') ? parsed.AbsoluteUri : parsed.AbsoluteUri + "/";
        return Uri.TryCreate(new Uri(basePath), StatusPath, out uri!);
    }
}
