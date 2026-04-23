using MediathekNext.Domain.Entities;
using MediathekNext.Domain.Enums;
using MediathekNext.Domain.Interfaces;
using MediathekNext.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MediathekNext.Infrastructure.Catalog;

public class EpisodeRepository(AppDbContext db) : IEpisodeRepository
{
    public async Task<Episode?> GetByIdAsync(string id, CancellationToken ct = default) =>
        await db.Episodes
            .Include(e => e.Show).ThenInclude(s => s.Channel)
            .Include(e => e.Streams)
            .FirstOrDefaultAsync(e => e.Id == id, ct);

    public async Task<IReadOnlyList<Episode>> SearchAsync(string query, CancellationToken ct = default)
    {
        var lower = query.ToLower();
        return await db.Episodes
            .Include(e => e.Show).ThenInclude(s => s.Channel)
            .Where(e =>
                e.Title.ToLower().Contains(lower) ||
                (e.Description != null && e.Description.ToLower().Contains(lower)) ||
                e.Show.Title.ToLower().Contains(lower))
            .OrderByDescending(e => e.BroadcastDate)
            .Take(200)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Episode>> GetByChannelAsync(
        string channelId,
        ContentType? contentType = null,
        CancellationToken ct = default)
    {
        var query = db.Episodes
            .Include(e => e.Show).ThenInclude(s => s.Channel)
            .Include(e => e.Streams)
            .Where(e => e.Show.Channel.Id == channelId);

        if (contentType.HasValue)
            query = query.Where(e => e.ContentType == contentType.Value);

        return await query.OrderByDescending(e => e.BroadcastDate).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Episode>> GetByShowAsync(
        string showId, CancellationToken ct = default) =>
        await db.Episodes
            .Include(e => e.Show).ThenInclude(s => s.Channel)
            .Include(e => e.Streams)
            .Where(e => e.ShowId == showId)
            .OrderByDescending(e => e.BroadcastDate)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Episode>> GetByContentTypeAsync(
        ContentType contentType,
        string? channelId = null,
        CancellationToken ct = default)
    {
        var query = db.Episodes
            .Include(e => e.Show).ThenInclude(s => s.Channel)
            .Include(e => e.Streams)
            .Where(e => e.ContentType == contentType);

        if (!string.IsNullOrEmpty(channelId))
            query = query.Where(e => e.Show.Channel.Id == channelId);

        return await query.OrderByDescending(e => e.BroadcastDate).ToListAsync(ct);
    }

    /// <summary>
    /// Returns shows with episode counts via SQL aggregation — does NOT load episodes into memory.
    /// </summary>
    public async Task<IReadOnlyList<(Show Show, int EpisodeCount, DateTimeOffset? LatestBroadcast)>> GetShowsAsync(
        string? channelId = null, CancellationToken ct = default)
    {
        var showQuery = db.Shows.Include(s => s.Channel).AsQueryable();
        if (!string.IsNullOrEmpty(channelId))
            showQuery = showQuery.Where(s => s.ChannelId == channelId);

        var shows = await showQuery.OrderBy(s => s.Title).ToListAsync(ct);
        var showIds = shows.Select(s => s.Id).ToList();

        // Single aggregation query for counts + latest date
        var stats = await db.Episodes
            .Where(e => showIds.Contains(e.ShowId))
            .GroupBy(e => e.ShowId)
            .Select(g => new { ShowId = g.Key, Count = g.Count(), Latest = g.Max(e => e.BroadcastDate) })
            .ToListAsync(ct);

        var statsMap = stats.ToDictionary(s => s.ShowId);

        return shows.Select(s =>
        {
            var count = statsMap.TryGetValue(s.Id, out var st) ? st.Count : 0;
            DateTimeOffset? latest = statsMap.TryGetValue(s.Id, out var st2)
                ? (DateTimeOffset?)st2.Latest
                : null;
            return (Show: s, EpisodeCount: count, LatestBroadcast: latest);
        }).ToList();
    }

    public async Task UpsertManyAsync(IEnumerable<Episode> episodes, CancellationToken ct = default)
    {
        const int batchSize = 500;
        var batch = new List<Episode>(batchSize);
        foreach (var episode in episodes)
        {
            batch.Add(episode);
            if (batch.Count >= batchSize) { await UpsertBatchAsync(batch, ct); batch.Clear(); }
        }
        if (batch.Count > 0) await UpsertBatchAsync(batch, ct);
    }

    private async Task UpsertBatchAsync(List<Episode> batch, CancellationToken ct)
    {
        var ids = batch.Select(e => e.Id).ToHashSet();
        var existing = await db.Episodes.Where(e => ids.Contains(e.Id)).Select(e => e.Id).ToHashSetAsync(ct);
        foreach (var episode in batch)
        {
            if (existing.Contains(episode.Id)) db.Episodes.Update(episode);
            else await db.Episodes.AddAsync(episode, ct);
        }
        await db.SaveChangesAsync(ct);
    }
}

