namespace Krautwatch.Domain.Options;

/// <summary>
/// Egress-proxy configuration for geo-restricted downloads (#45), bound from the host's
/// <c>Download</c> config section. Two independent modes:
/// <list type="bullet">
///   <item><b>Bring-your-own</b> — <see cref="ProxyUrl"/>: a proxy the operator controls (their own DE
///   VPS / WireGuard exit). Recommended, trusted, always tried first.</item>
///   <item><b>Auto public list</b> — <see cref="ProxyList"/>: opt-in convenience that sources free DE
///   proxies from a public list. Best-effort/untrusted; only content integrity-checked downloads.</item>
/// </list>
/// Lives in Domain because both Application (refresh scheduler) and Infrastructure (source + selector)
/// bind to it, and both layers may only depend on Domain.
/// </summary>
public class EgressProxyOptions
{
    public const string SectionName = "Download";

    /// <summary>Bring-your-own proxy URL (e.g. <c>http://10.0.0.9:3128</c>). Empty = not configured.</summary>
    public string? ProxyUrl { get; set; }

    public ProxyListOptions ProxyList { get; set; } = new();
}

/// <summary>Auto public-list mode (Mode B). Opt-in; refreshed on a schedule into the <c>Proxy</c> table.</summary>
public class ProxyListOptions
{
    /// <summary>Master switch for Mode B. Off by default — bring-your-own is the recommended path.</summary>
    public bool Enabled { get; set; }

    /// <summary>How often to refresh the cached list. Default once a day.</summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromDays(1);

    /// <summary>Grace period after startup before the first refresh.</summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Public proxy-list endpoint (GeoNode by default), already filtered to the target country.</summary>
    public string SourceUrl { get; set; } =
        "https://proxylist.geonode.com/api/proxy-list?limit=100&page=1&sort_by=lastChecked&sort_type=desc&country=DE&protocols=http%2Chttps";

    /// <summary>Country the egress must resolve to (informational; the source URL already filters).</summary>
    public string Country { get; set; } = "DE";

    /// <summary>How many ranked candidates the selector offers per geo-restricted download.</summary>
    public int MaxCandidates { get; set; } = 5;
}
