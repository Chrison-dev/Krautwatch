using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Interfaces;

namespace Krautwatch.Application.Downloads;

/// <summary>
/// The SABnzbd "add" path: a download token (an <c>Episode.Id</c>) is resolved to its best stream,
/// a <see cref="DownloadJob"/> is created and enqueued, and the job id is handed back as SABnzbd's
/// <c>nzo_id</c>. Returns null when the token is unknown or the episode has no stream.
/// </summary>
public class AddDownloadByTokenHandler(
    IEpisodeRepository episodes,
    IDownloadJobRepository jobs,
    IDownloadQueue queue)
{
    public async Task<Guid?> HandleAsync(string token, CancellationToken ct = default)
    {
        var episode = await episodes.GetByIdAsync(token, ct);
        if (episode is null) return null;

        var stream = episode.Streams.OrderByDescending(s => s.Quality).FirstOrDefault();
        if (stream is null) return null;

        var job = new DownloadJob
        {
            Id        = Guid.NewGuid(),
            EpisodeId = episode.Id,
            Episode   = episode,
            StreamUrl = stream.Url,
            Quality   = stream.Quality,
        };

        await jobs.AddAsync(job, ct);
        await queue.EnqueueAsync(job.Id, stream.Url, ct);
        return job.Id;
    }
}
