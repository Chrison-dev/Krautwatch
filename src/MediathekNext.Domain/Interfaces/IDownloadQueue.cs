namespace MediathekNext.Domain.Interfaces;

/// <summary>
/// Abstraction over the job scheduling mechanism (TickerQ in production).
/// Keeps the Application layer free of TickerQ dependencies.
/// Implemented in Infrastructure by TickerQDownloadQueue.
/// </summary>
public interface IDownloadQueue
{
    /// <summary>Enqueue Phase 1 (Resolve) for a new download job.</summary>
    Task EnqueueAsync(Guid downloadJobId, string streamUrl, CancellationToken ct = default);

    /// <summary>Re-enqueue Phase 1 for a retry — identical to a fresh enqueue.</summary>
    Task RequeueAsync(Guid downloadJobId, string streamUrl, CancellationToken ct = default);
}
