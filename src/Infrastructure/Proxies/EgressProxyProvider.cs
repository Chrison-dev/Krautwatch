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
public sealed class EgressProxyProvider(
    EgressSettingsSource settings, EgressProxyOptions options, IServiceScopeFactory scopes)
    : IEgressProxyProvider
{
    public async Task<IReadOnlyList<string>> GetCandidatesAsync(CancellationToken ct = default)
    {
        // Read every call rather than at construction: these are editable in the UI now (#54), and this
        // is a singleton that would otherwise hold whatever was configured at process start.
        var effective = settings.Current;

        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(effective.ProxyUrl))
            candidates.Add(effective.ProxyUrl.Trim());

        if (effective.ProxyListEnabled)
        {
            using var scope = scopes.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IProxyRepository>();
            // Country and source URL stay configuration-only: they are tuning knobs for the public list,
            // not first-run decisions, and nothing in the UI would know what to do with them.
            var ranked = await repo.GetRankedAsync(
                options.ProxyList.Country, effective.MaxCandidates, ct);
            candidates.AddRange(ranked.Select(p => p.Url));
        }

        return candidates.Distinct().ToList();
    }

    public async Task ReportResultAsync(string proxyUrl, bool ok, CancellationToken ct = default)
    {
        if (!settings.Current.ProxyListEnabled) return; // only list rows have feedback columns to update
        try
        {
            using var scope = scopes.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IProxyRepository>();
            await repo.RecordProbeResultAsync(proxyUrl, ok, ct);
        }
        catch { /* feedback is best-effort — never fail a download over it */ }
    }
}
