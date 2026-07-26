namespace Krautwatch.Domain.Entities;

/// <summary>
/// A cached public egress-proxy candidate for Mode B (#45). Rows are refreshed on a schedule from a
/// public list (GeoNode). Carries the source's own quality metrics <b>and</b> our feedback from real
/// download attempts, so selection can rank best-first and learn which proxies actually work.
/// </summary>
public class Proxy
{
    /// <summary>Stable key: <c>"{host}:{port}"</c>.</summary>
    public string Id { get; init; } = default!;

    public string Host { get; init; } = default!;
    public int Port { get; init; }
    public string Protocol { get; init; } = "http";
    public string Source { get; init; } = default!;   // which list it came from, e.g. "geonode"

    // ── Source-reported quality (the list's own measurements) ──────────────
    public string? Country { get; init; }
    public double UpTime { get; set; }                 // percentage 0–100
    public int Speed { get; set; }                     // higher = faster
    public int ResponseTime { get; set; }              // ms, lower = better
    public double Latency { get; set; }                // seconds, lower = better
    public string? AnonymityLevel { get; set; }
    public DateTimeOffset? SourceLastChecked { get; set; }

    // ── Our own feedback from real fetch attempts ──────────────────────────
    public bool? LastProbeOk { get; set; }
    public DateTimeOffset? LastProbedAt { get; set; }
    public string? VerifiedEgressCountry { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>The proxy URL the download client dials, e.g. <c>http://1.2.3.4:3128</c>.</summary>
    public string Url => $"{Protocol}://{Host}:{Port}";
}
