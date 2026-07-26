using Krautwatch.Domain.Interfaces;
using Krautwatch.Domain.Options;
using Microsoft.Extensions.DependencyInjection;

namespace Krautwatch.Infrastructure.Proxies;

/// <summary>
/// Resolves egress-proxy candidates for geo-restricted downloads (#45): the bring-your-own
/// <see cref="EgressProxyOptions.ProxyUrl"/> first (trusted, always preferred), then — if Mode B is
/// enabled — the ranked public-list rows. A singleton that opens its own scope to reach the scoped
/// <see cref="IProxyRepository"/>, so it can be injected into the singleton download providers.
/// </summary>
public sealed class EgressProxyProvider(EgressProxyOptions options, IServiceScopeFactory scopes)
    : IEgressProxyProvider
{
    public async Task<IReadOnlyList<string>> GetCandidatesAsync(CancellationToken ct = default)
    {
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(options.ProxyUrl))
            candidates.Add(options.ProxyUrl.Trim());

        if (options.ProxyList.Enabled)
        {
            using var scope = scopes.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IProxyRepository>();
            var ranked = await repo.GetRankedAsync(options.ProxyList.Country, options.ProxyList.MaxCandidates, ct);
            candidates.AddRange(ranked.Select(p => p.Url));
        }

        return candidates.Distinct().ToList();
    }

    public async Task ReportResultAsync(string proxyUrl, bool ok, CancellationToken ct = default)
    {
        if (!options.ProxyList.Enabled) return; // only list rows have feedback columns to update
        try
        {
            using var scope = scopes.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IProxyRepository>();
            await repo.RecordProbeResultAsync(proxyUrl, ok, ct);
        }
        catch { /* feedback is best-effort — never fail a download over it */ }
    }
}
