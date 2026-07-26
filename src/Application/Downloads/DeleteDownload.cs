using Krautwatch.Domain.Interfaces;

namespace Krautwatch.Application.Downloads;

/// <summary>Removes a download job from the queue/history (the downloaded file, if any, is left on disk).</summary>
public class DeleteDownloadHandler(IDownloadJobRepository jobs)
{
    public Task HandleAsync(Guid jobId, CancellationToken ct = default) => jobs.DeleteAsync(jobId, ct);
}
