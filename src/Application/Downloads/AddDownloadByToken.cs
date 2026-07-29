using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Interfaces;
using Krautwatch.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Krautwatch.Application.Downloads;

/// <summary>
/// The SABnzbd "add" path: a download token is resolved to its best stream, a <see cref="DownloadJob"/> is
/// created and enqueued, and the job id is handed back as SABnzbd's <c>nzo_id</c>. Returns null when the
/// token is unknown or the episode has no stream.
/// </summary>
/// <remarks>
/// Also the point where we <b>learn</b>. A grab is a deliberate pick out of the candidates the indexer
/// offered, so when the token carries a TVDB id we count that pick against the show — which is how an
/// ambiguous id eventually resolves itself without anyone visiting a settings page.
/// </remarks>
public class AddDownloadByTokenHandler(
    IEpisodeRepository episodes,
    IDownloadJobRepository jobs,
    IDownloadQueue queue,
    IShowMappingRepository? mappings = null,
    ILogger<AddDownloadByTokenHandler>? logger = null)
{
    public async Task<Guid?> HandleAsync(string token, CancellationToken ct = default)
    {
        var parsed = ReleaseToken.Parse(token);

        var episode = await episodes.GetByIdAsync(parsed.EpisodeId, ct);
        if (episode is null) return null;

        await RecordPickAsync(parsed, episode, ct);

        var stream = episode.Streams.OrderByDescending(s => s.Quality).FirstOrDefault();
        if (stream is null) return null;

        var job = new DownloadJob
        {
            Id            = Guid.NewGuid(),
            EpisodeId     = episode.Id,
            Episode       = episode,
            StreamUrl     = stream.Url,
            Quality       = stream.Quality,
            GeoRestricted = episode.GeoRestricted,
        };

        await jobs.AddAsync(job, ct);
        await queue.EnqueueAsync(job.Id, stream.Url, ct);
        return job.Id;
    }

    /// <summary>
    /// Counts this grab as a vote that <paramref name="episode"/>'s show answers the token's TVDB id.
    /// </summary>
    /// <remarks>
    /// Deliberately best-effort: a failure to learn must never fail the download the operator actually
    /// asked for. Losing a vote costs us one repetition; losing the download is a visible bug.
    /// </remarks>
    private async Task RecordPickAsync(ReleaseToken token, Episode episode, CancellationToken ct)
    {
        if (mappings is null || token.TvdbId is not { } tvdbId)
            return;

        try
        {
            var picks = await mappings.RecordPickAsync(tvdbId, episode.ShowId, ct);
            logger?.LogInformation(
                "Grab counted: {ShowId} picked for TVDB {TvdbId} ({Picks} time(s))",
                episode.ShowId, tvdbId, picks);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex,
                "Could not record the grab of {ShowId} for TVDB {TvdbId}; the download continues",
                episode.ShowId, tvdbId);
        }
    }
}
