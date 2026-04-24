using Mediathek.Data;
using Mediathek.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Mediathek.Crawlers;

// ── Intermediate crawl result ─────────────────────────────────────────────────
// Both ARD and ZDF crawlers produce these; the upsert service writes them to DB.

public sealed record CrawlResult(
    string BroadcasterKey,
    string ShowTitle,
    string? ShowExternalId,
    string EpisodeTitle,
    string? Description,
    DateTimeOffset? BroadcastTime,
    TimeSpan? Duration,
    string? WebsiteUrl,
    string? ThumbnailUrl,
    GeoRestriction Geo,
    string? EpisodeExternalId,
    IReadOnlyList<StreamEntry> Streams,
    IReadOnlyList<SubtitleEntry> Subtitles
);

public sealed record StreamEntry(
    StreamQuality Quality,
    StreamLanguage Language,
    string Url,
    bool IsHls = false
);

public sealed record SubtitleEntry(
    StreamLanguage Language,
    string Url
);

// ── Upsert service ────────────────────────────────────────────────────────────

public class CrawlResultPersister(MediathekDbContext db, ILogger<CrawlResultPersister> log)
{
    // Cache broadcaster key -> id to avoid repeated lookups
    private Dictionary<string, int>? _broadcasterCache;

    public async Task PersistBatchAsync(
        IEnumerable<CrawlResult> results, CancellationToken ct = default)
    {
        _broadcasterCache ??= await db.Broadcasters
            .ToDictionaryAsync(b => b.Key, b => b.Id, ct);

        int saved = 0;
        foreach (var r in results)
        {
            try
            {
                await UpsertOneAsync(r, ct);
                saved++;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Failed to persist episode {Title}", r.EpisodeTitle);
            }
        }

        await db.SaveChangesAsync(ct);
        log.LogInformation("Persisted {Count} episodes", saved);
    }

    private async Task UpsertOneAsync(CrawlResult r, CancellationToken ct)
    {
        if (!_broadcasterCache!.TryGetValue(r.BroadcasterKey, out var broadcasterId))
        {
            log.LogWarning("Unknown broadcaster key {Key} — skipping", r.BroadcasterKey);
            return;
        }

        // ── Show: find or create ──────────────────────────────────────────────
        var show = r.ShowExternalId is not null
            ? await db.Shows.FirstOrDefaultAsync(
                s => s.BroadcasterId == broadcasterId && s.ExternalId == r.ShowExternalId, ct)
            : await db.Shows.FirstOrDefaultAsync(
                s => s.BroadcasterId == broadcasterId && s.Title == r.ShowTitle, ct);

        if (show is null)
        {
            show = new Show
            {
                BroadcasterId = broadcasterId,
                Title         = r.ShowTitle,
                ExternalId    = r.ShowExternalId,
            };
            db.Shows.Add(show);
            await db.SaveChangesAsync(ct); // need the ID before adding episodes
        }

        // ── Episode: find or create ───────────────────────────────────────────
        var episode = r.EpisodeExternalId is not null
            ? await db.Episodes
                .Include(e => e.Streams)
                .Include(e => e.Subtitles)
                .FirstOrDefaultAsync(
                    e => e.ShowId == show.Id && e.ExternalId == r.EpisodeExternalId, ct)
            : await db.Episodes
                .Include(e => e.Streams)
                .Include(e => e.Subtitles)
                .FirstOrDefaultAsync(
                    e => e.ShowId == show.Id
                      && e.Title  == r.EpisodeTitle
                      && e.AiredOn == (r.BroadcastTime.HasValue
                            ? DateOnly.FromDateTime(r.BroadcastTime.Value.Date)
                            : null), ct);

        if (episode is null)
        {
            episode = new Episode { ShowId = show.Id };
            db.Episodes.Add(episode);
        }

        // Update episode fields (always overwrite — fresh data wins)
        episode.Title        = r.EpisodeTitle;
        episode.Description  = r.Description;
        episode.Duration     = r.Duration;
        episode.WebsiteUrl   = r.WebsiteUrl;
        episode.ThumbnailUrl = r.ThumbnailUrl;
        episode.Geo          = r.Geo;
        episode.ExternalId   = r.EpisodeExternalId;
        episode.ImportedAt   = DateTimeOffset.UtcNow;

        if (r.BroadcastTime.HasValue)
        {
            episode.AiredOn = DateOnly.FromDateTime(r.BroadcastTime.Value.Date);
            episode.AiredAt = TimeOnly.FromDateTime(r.BroadcastTime.Value.DateTime);
        }

        // ── Streams: replace all ──────────────────────────────────────────────
        db.Streams.RemoveRange(episode.Streams);
        foreach (var s in r.Streams)
        {
            db.Streams.Add(new EpisodeStream
            {
                Episode  = episode,
                Quality  = s.Quality,
                Language = s.Language,
                Url      = s.Url,
                IsHls    = s.IsHls,
            });
        }

        // ── Subtitles: replace all ────────────────────────────────────────────
        db.Subtitles.RemoveRange(episode.Subtitles);
        foreach (var s in r.Subtitles)
        {
            db.Subtitles.Add(new EpisodeSubtitle
            {
                Episode  = episode,
                Language = s.Language,
                Url      = s.Url,
            });
        }
    }
}
