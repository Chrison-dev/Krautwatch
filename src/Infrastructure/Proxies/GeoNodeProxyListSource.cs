using System.Text.Json;
using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Interfaces;
using Krautwatch.Domain.Options;
using Microsoft.Extensions.Logging;

namespace Krautwatch.Infrastructure.Proxies;

/// <summary>
/// Fetches egress-proxy candidates from GeoNode's free public list API (#45). The endpoint is already
/// country/protocol-filtered by <see cref="ProxyListOptions.SourceUrl"/>; we map its quality fields onto
/// <see cref="Proxy"/>. Parsing is tolerant — the list is best-effort and fields come and go.
/// </summary>
public sealed class GeoNodeProxyListSource(HttpClient http, ProxyListOptions options, ILogger<GeoNodeProxyListSource> logger)
    : IProxyListSource
{
    public async Task<IReadOnlyList<Proxy>> FetchAsync(CancellationToken ct = default)
    {
        try
        {
            using var stream = await http.GetStreamAsync(options.SourceUrl, ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return [];

            var proxies = new List<Proxy>();
            foreach (var e in data.EnumerateArray())
            {
                var proxy = Map(e);
                if (proxy is not null) proxies.Add(proxy);
            }

            logger.LogInformation("Fetched {Count} proxy candidates from the public list.", proxies.Count);
            return proxies;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch the public proxy list — keeping the cached rows.");
            return [];
        }
    }

    private static Proxy? Map(JsonElement e)
    {
        var ip = Str(e, "ip");
        var portStr = Str(e, "port");
        if (ip is null || !int.TryParse(portStr, out var port)) return null;

        return new Proxy
        {
            Id = $"{ip}:{port}",
            Host = ip,
            Port = port,
            Protocol = "http",       // we dial an HTTP CONNECT proxy regardless of the listed protocol
            Source = "geonode",
            Country = Str(e, "country"),
            UpTime = Num(e, "upTime"),
            Speed = (int)Num(e, "speed"),
            ResponseTime = (int)Num(e, "responseTime"),
            Latency = Num(e, "latency"),
            AnonymityLevel = Str(e, "anonymityLevel"),
            SourceLastChecked = e.TryGetProperty("lastChecked", out var lc) && lc.ValueKind == JsonValueKind.Number
                ? DateTimeOffset.FromUnixTimeSeconds(lc.GetInt64())
                : null,
        };
    }

    private static string? Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double Num(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
}
