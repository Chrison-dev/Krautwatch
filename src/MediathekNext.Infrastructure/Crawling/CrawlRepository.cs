using System.Security.Cryptography;
using System.Text;
using MediathekNext.Crawlers.Core;
using MediathekNext.Domain.Entities;
using MediathekNext.Domain.Enums;
using MediathekNext.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MediathekNext.Infrastructure.Crawling;

/// <summary>
/// Persists a single CrawlResult to the database immediately.
/// Maps the broadcaster-agnostic CrawlResult onto the Domain entities
/// (Channel, Show, Episode, EpisodeStream).
/// </summary>
public sealed class CrawlRepository(AppDbContext db) : ICrawlRepository
{
    public async Task UpsertAsync(CrawlResult result, CancellationToken ct = default)
    {
        // ── 1. Channel ─────────────────────────────────────────────────────────
        var channelId = result.BroadcasterKey.ToLowerInvariant();
        if (!await db.Channels.AnyAsync(c => c.Id == channelId, ct))
        {
            db.Channels.Add(new Channel
            {
                Id          = channelId,
                Name        = result.BroadcasterKey,
                ProviderKey = "crawler",
            });
        }

        // ── 2. Show ────────────────────────────────────────────────────────────
        var showId = MakeId(channelId + ":show:" + result.ShowTitle);
        if (!await db.Shows.AnyAsync(s => s.Id == showId, ct))
        {
            db.Shows.Add(new Show
            {
                Id        = showId,
                Title     = result.ShowTitle,
                ChannelId = channelId,
            });
        }

        // ── 3. Episode ─────────────────────────────────────────────────────────
        var episodeId = result.EpisodeExternalId
                     ?? MakeId(showId + ":" + result.EpisodeTitle + ":" + result.BroadcastTime?.ToString("O"));

        var existing = await db.Episodes
            .Include(e => e.Streams)
            .FirstOrDefaultAsync(e => e.Id == episodeId, ct);

        if (existing is null)
        {
            db.Episodes.Add(new Episode
            {
                Id            = episodeId,
                ShowId        = showId,
                Title         = result.EpisodeTitle,
                Description   = result.Description,
                BroadcastDate = result.BroadcastTime ?? DateTimeOffset.UtcNow,
                Duration      = result.Duration ?? TimeSpan.Zero,
                ContentType   = ContentType.Episode,
            });
        }
        else
        {
            // Replace streams only — episode metadata considered stable after first insert
            db.EpisodeStreams.RemoveRange(existing.Streams);
        }

        // ── 4. Streams ─────────────────────────────────────────────────────────
        foreach (var stream in result.Streams)
        {
            db.EpisodeStreams.Add(new EpisodeStream
            {
                Id        = MakeId(episodeId + ":" + stream.Quality + ":" + stream.Language + ":" + stream.Url),
                EpisodeId = episodeId,
                Quality   = MapQuality(stream.Quality),
                Format    = stream.IsHls ? "m3u8" : "mp4",
                Url       = stream.Url,
            });
        }

        await db.SaveChangesAsync(ct);
    }

    private static VideoQuality MapQuality(StreamQuality q) => q switch
    {
        StreamQuality.Sd     => VideoQuality.Low,
        StreamQuality.Normal => VideoQuality.Standard,
        StreamQuality.Hd     => VideoQuality.High,
        StreamQuality.Uhd    => VideoQuality.High,
        _                    => VideoQuality.Standard,
    };

    private static string MakeId(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash)[..32].ToLowerInvariant();
    }
}
