using Krautwatch.Domain.Interfaces;

namespace Krautwatch.Infrastructure.Downloads;

/// <summary>
/// Placeholder <see cref="IDownloadQueue"/> — a no-op until the Downloader agent and the
/// Wolverine download-messaging land (DR-009 fleet). The old TickerQ-based queue was removed
/// with the rest of the dead TickerQ pipeline.
/// </summary>
public class NullDownloadQueue : IDownloadQueue
{
    public Task EnqueueAsync(Guid downloadJobId, string streamUrl, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task RequeueAsync(Guid downloadJobId, string streamUrl, CancellationToken ct = default)
        => Task.CompletedTask;
}
