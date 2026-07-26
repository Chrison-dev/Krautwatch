namespace Krautwatch.Domain.Interfaces;

/// <summary>
/// Supplies egress-proxy candidates for <b>geo-restricted</b> downloads (#45). The fence is enforced
/// purely by country-of-egress, so a geo-restricted stream is only reachable through an in-region
/// (German) proxy. The Downloader consults this only when a job is geo-restricted; an empty result
/// means no egress is configured and the job must fail fast.
/// </summary>
public interface IEgressProxyProvider
{
    /// <summary>Proxy URLs to try, best-first, for a geo-restricted fetch. Empty = none configured.</summary>
    Task<IReadOnlyList<string>> GetCandidatesAsync(CancellationToken ct = default);

    /// <summary>
    /// Feeds a real fetch outcome back so ranking can improve over time. Best-effort and never throws;
    /// a no-op for a bring-your-own proxy.
    /// </summary>
    Task ReportResultAsync(string proxyUrl, bool ok, CancellationToken ct = default);
}
