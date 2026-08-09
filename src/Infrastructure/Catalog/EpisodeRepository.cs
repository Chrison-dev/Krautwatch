using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Enums;
using Krautwatch.Domain.Interfaces;
using Krautwatch.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Krautwatch.Infrastructure.Catalog;

public class EpisodeRepository(AppDbContext db) : IEpisodeRepository
{
    public async Task<Episode?> GetByIdAsync(string id, CancellationToken ct = default) =>
        await db.Episodes
            .Include(e => e.Show).ThenInclude(s => s.Channel)
            .Include(e => e.Streams)
            .FirstOrDefaultAsync(e => e.Id == id, ct);

    public async Task<IReadOnlyList<Episode>> GetByTvdbIdAsync(int tvdbId, CancellationToken ct = default) =>
        await db.Episodes
            .Include(e => e.Show).ThenInclude(s => s.Channel)
            .Include(e => e.Streams)
            .Where(e => e.Show.TvdbId == tvdbId)
            .OrderByDescending(e => e.BroadcastDate)
            .Take(200)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Episode>> SearchAsync(string query, CancellationToken ct = default)
    {
        var lower = query.ToLower();
        return await db.Episodes
            .Include(e => e.Show).ThenInclude(s => s.Channel)
            .Include(e => e.Streams)
            .Where(e =>
                e.Title.ToLower().Contains(lower) ||
                (e.Description != null && e.Description.ToLower().Contains(lower)) ||
                e.Show.Title.ToLower().Contains(lower))
            .OrderByDescending(e => e.BroadcastDate)
            .Take(200)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Episode>> GetByBroadcastDateAsync(
        DateOnly date, CancellationToken ct = default)
    {
        // Compared as the *broadcast* date rather than UTC: a 20:30 Berlin broadcast is the same UTC day
        // but a 01:00 one is the day before, and matching UTC would lose late-night shows entirely.
        var from = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddDays(-1);
        var to = from.AddDays(3);

        var window = await db.Episodes
            .Include(e => e.Show).ThenInclude(s => s!.Channel)
            .Include(e => e.Streams)
            .Where(e => e.BroadcastDate >= from && e.BroadcastDate < to)
            .ToListAsync(ct);

        return window
            .Where(e => DateOnly.FromDateTime(e.BroadcastDate.DateTime) == date)
            .ToList();
    }

    public async Task<IReadOnlyList<Episode>> GetRecentAsync(int limit, CancellationToken ct = default) =>
        await db.Episodes
            .Include(e => e.Show).ThenInclude(s => s.Channel)
            .OrderByDescending(e => e.BroadcastDate)
            .Take(limit)
            .ToListAsync(ct);

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

    // Upserts a whole crawl graph (Channel → Show → Episode → EpisodeStream). Crawlers build fresh,
    // untracked entities and share one Channel/Show instance across a batch, so we set each entity's
    // state by existence — INSERT when new, UPDATE when present — instead of blindly re-inserting
    // (which would duplicate-key on the shared Channel/Show or throw on a not-yet-persisted row).
    private async Task UpsertBatchAsync(List<Episode> batch, CancellationToken ct)
    {
        db.ChangeTracker.Clear();

        // Channels first (distinct by id) so Shows can reference already-tracked channels.
        foreach (var channel in DistinctById(batch.Select(e => e.Show?.Channel)))
        {
            var exists = await db.Channels.AnyAsync(c => c.Id == channel.Id, ct);
            db.Entry(channel).State = exists ? EntityState.Modified : EntityState.Added;
        }

        foreach (var show in DistinctById(batch.Select(e => e.Show)))
        {
            var exists = await db.Shows.AnyAsync(s => s.Id == show.Id, ct);
            db.Entry(show).State = exists ? EntityState.Modified : EntityState.Added;
        }

        var ids = batch.Select(e => e.Id).ToHashSet();
        var existingEpisodes = await db.Episodes
            .Where(e => ids.Contains(e.Id)).Select(e => e.Id).ToHashSetAsync(ct);

        foreach (var episode in batch)
        {
            var episodeExists = existingEpisodes.Contains(episode.Id);
            db.Entry(episode).State = episodeExists ? EntityState.Modified : EntityState.Added;

            // Setting an entry's state explicitly does not cascade to children, so stream rows are
            // stated individually. Stream ids are derived from the episode id and stable across crawls.
            foreach (var stream in episode.Streams)
            {
                var streamExists = episodeExists && await db.EpisodeStreams.AnyAsync(s => s.Id == stream.Id, ct);
                db.Entry(stream).State = streamExists ? EntityState.Modified : EntityState.Added;
            }
        }

        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    private static IEnumerable<T> DistinctById<T>(IEnumerable<T?> items) where T : class =>
        items.Where(x => x is not null)
             .GroupBy(x => KeyOf(x!))
             .Select(g => g.First()!);

    private static string KeyOf(object entity) => entity switch
    {
        Channel c => c.Id,
        Show s => s.Id,
        _ => entity.GetHashCode().ToString(),
    };
}

