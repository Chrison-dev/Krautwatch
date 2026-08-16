using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Krautwatch.Infrastructure.Crawling.Zdf;

/// <summary>
/// Reports a rejected ZDF <c>Api-Auth</c> key on <c>/health</c>, so a rotation is visible in the Aspire
/// dashboard and to any monitoring rather than only in the agent's logs (#13).
/// </summary>
/// <remarks>
/// <para>
/// <b>Degraded, never Unhealthy</b>, and that is a deliberate limit. <c>/health</c> answers 503 for
/// Unhealthy, and that endpoint is what the AppHost's health check and the generated compose file
/// probe — so reporting Unhealthy would restart the ZDF agent in a loop over a condition no restart
/// can fix, and take the "is this container up?" signal with it. Degraded answers 200 and still shows
/// up amber everywhere that reads the report.
/// </para>
/// <para>
/// It reports on the first rejection rather than after a threshold. The client does not retry an
/// auth failure — retrying a rotated key is pointless — so one rejection already means every ZDF
/// crawl since then has failed.
/// </para>
/// </remarks>
public sealed class ZdfAuthHealthCheck(ZdfAuthState state, TimeProvider? timeProvider = null) : IHealthCheck
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var snapshot = state.Snapshot();

        if (!snapshot.IsRejected)
            return Task.FromResult(HealthCheckResult.Healthy("ZDF is accepting our Api-Auth key."));

        var since = snapshot.FirstRejectionAt is { } first
            ? _time.GetUtcNow() - first
            : TimeSpan.Zero;

        return Task.FromResult(HealthCheckResult.Degraded(
            $"ZDF has rejected our Api-Auth key {snapshot.ConsecutiveRejections} time(s) over " +
            $"{since.TotalMinutes:0} minute(s), last with {(int?)snapshot.LastStatusCode}. The key has " +
            $"most likely been rotated — set {ZdfOptions.SectionName}:{nameof(ZdfOptions.ApiAuthKey)} " +
            "to the current value and restart. ZDF crawling produces nothing until then.",
            data: new Dictionary<string, object>
            {
                ["consecutiveRejections"] = snapshot.ConsecutiveRejections,
                ["firstRejectionAt"] = snapshot.FirstRejectionAt?.ToString("O") ?? "",
                ["lastStatusCode"] = (int?)snapshot.LastStatusCode ?? 0,
            }));
    }
}
