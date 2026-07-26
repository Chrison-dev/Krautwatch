using Krautwatch.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace Krautwatch.Application.Downloads;

/// <summary>
/// The proxy-list refresh <b>Action</b> (Mode B, #45): pulls the current public list and upserts it
/// into the cached <c>Proxy</c> table, preserving our probe feedback. Run on a schedule by
/// <see cref="ProxyRefreshService"/>. A fetch that returns nothing leaves the cached rows untouched.
/// </summary>
public class RefreshProxyListHandler(
    IProxyListSource source, IProxyRepository proxies, ILogger<RefreshProxyListHandler> logger)
{
    public async Task HandleAsync(CancellationToken ct = default)
    {
        var fetched = await source.FetchAsync(ct);
        if (fetched.Count == 0)
        {
            logger.LogInformation("Proxy-list refresh returned no candidates — keeping the cached rows.");
            return;
        }

        await proxies.UpsertBatchAsync(fetched, ct);
        logger.LogInformation("Proxy-list refresh upserted {Count} candidates.", fetched.Count);
    }
}
