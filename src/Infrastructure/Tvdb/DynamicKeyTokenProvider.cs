using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tvdb.Abstractions;

namespace Krautwatch.Infrastructure.Tvdb;

/// <summary>
/// Acquires TVDB bearer tokens using the key resolved at call time by <see cref="TvdbApiKeySource"/>.
/// </summary>
/// <remarks>
/// <para>
/// Replaces the client library's own provider, which reads the key once from <c>IOptions</c> and is a
/// singleton — so a key entered in the settings UI would not take effect until a restart. The library
/// registers its provider with <c>TryAddSingleton</c>, which is precisely the seam for substituting one:
/// registering ours first wins.
/// </para>
/// <para>
/// A token is cached until the key changes or it expires. TVDB tokens last a month, so re-login is rare;
/// the key comparison is what makes a rotation take effect immediately.
/// </para>
/// </remarks>
public class DynamicKeyTokenProvider(
    TvdbApiKeySource keys,
    IOptions<TvdbConfiguration> options,
    IHttpClientFactory httpClientFactory,
    ILogger<DynamicKeyTokenProvider> logger) : ITokenProvider
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _tokenForKey;

    public Token Token { get; private set; } = default!;

    public async Task<Token> AcquireTokenAsync(CancellationToken cancellationToken = default)
    {
        var key = keys.Current
            ?? throw new InvalidOperationException(
                "No TVDB API key is configured. Set TvdbConfiguration:ApiKey, or enter one in Settings.");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = Token;
            if (current is not null && !current.IsTokenExpired && _tokenForKey == key)
                return current;

            var loginUrl = options.Value.TokenUrl;
            using var client = httpClientFactory.CreateClient(nameof(DynamicKeyTokenProvider));

            var response = await client.PostAsJsonAsync(
                loginUrl, new LoginRequest(key, keys.Pin), cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogError("TVDB login failed with {Status}: {Body}", (int)response.StatusCode, body);
                response.EnsureSuccessStatusCode();
            }

            var envelope = await response.Content.ReadFromJsonAsync<LoginEnvelope>(cancellationToken);
            var token = envelope?.Data?.Token;
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("TVDB login returned no token.");

            Token = new Token { AccessToken = token };
            _tokenForKey = key;
            logger.LogInformation("Acquired a TVDB token (key from {Origin})", keys.Origin);
            return Token;
        }
        finally
        {
            _gate.Release();
        }
    }

    private record LoginRequest(
        [property: JsonPropertyName("apikey")] string ApiKey,
        [property: JsonPropertyName("pin")] string? Pin);

    private record LoginEnvelope([property: JsonPropertyName("data")] LoginData? Data);

    private record LoginData([property: JsonPropertyName("token")] string? Token);
}
