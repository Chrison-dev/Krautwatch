using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Interfaces;
using Krautwatch.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Krautwatch.Infrastructure.Proxies;

/// <summary>
/// Persists the cached public proxy list (Mode B, #45). Refreshes upsert the source-reported metrics
/// while <b>preserving our feedback columns</b> (probe outcome), and selection ranks best-first.
/// </summary>
public class ProxyRepository(AppDbContext db) : IProxyRepository
{
    public async Task UpsertBatchAsync(IEnumerable<Proxy> proxies, CancellationToken ct = default)
    {
        var incoming = proxies
            .GroupBy(p => p.Id).Select(g => g.First()) // de-dupe by host:port
            .ToList();
        if (incoming.Count == 0) return;

        var ids = incoming.Select(p => p.Id).ToList();
        var existing = await db.Proxies.Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var now = DateTimeOffset.UtcNow;

        foreach (var p in incoming)
        {
            if (existing.TryGetValue(p.Id, out var row))
            {
                // Refresh the source metrics; keep CreatedAt and our probe feedback.
                row.UpTime = p.UpTime;
                row.Speed = p.Speed;
                row.ResponseTime = p.ResponseTime;
                row.Latency = p.Latency;
                row.AnonymityLevel = p.AnonymityLevel;
                row.SourceLastChecked = p.SourceLastChecked;
                row.UpdatedAt = now;
            }
            else
            {
                p.UpdatedAt = now;
                db.Proxies.Add(p);
            }
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<Proxy>> GetRankedAsync(string country, int limit, CancellationToken ct = default)
    {
        // The table is small (~a list page); pull the country's rows and rank in memory so the
        // composite ordering (probe feedback → uptime → speed → recency) stays legible and portable.
        var rows = await db.Proxies
            .Where(p => p.VerifiedEgressCountry == country || p.Country == country)
            .ToListAsync(ct);

        return rows
            .OrderByDescending(p => p.LastProbeOk == true)   // known-good first
            .ThenBy(p => p.LastProbeOk == false)             // known-bad last
            .ThenByDescending(p => p.UpTime)
            .ThenByDescending(p => p.Speed)
            .ThenByDescending(p => p.SourceLastChecked)
            .Take(limit)
            .ToList();
    }

    public async Task RecordProbeResultAsync(string proxyUrl, bool ok, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(proxyUrl, UriKind.Absolute, out var uri)) return;
        var id = $"{uri.Host}:{uri.Port}";
        await db.Proxies.Where(p => p.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.LastProbeOk, ok)
                .SetProperty(p => p.LastProbedAt, DateTimeOffset.UtcNow), ct);
    }
}
