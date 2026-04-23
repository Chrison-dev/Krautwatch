using MediathekNext.Domain.Interfaces;
using MediathekNext.Infrastructure.Jobs;
// using TickerQ.Utilities.Entities; // TODO: Add proper TickerQ reference
// using TickerQ.Utilities.Interfaces.Managers; // TODO: Add proper TickerQ reference

namespace MediathekNext.Infrastructure.Downloads;

/// <summary>
/// Implements IDownloadQueue by enqueuing TickerQ TimeTicker jobs.
/// The Application layer depends only on the domain interface —
/// TickerQ is an infrastructure detail it never sees.
/// TODO: Implement proper TickerQ integration
/// </summary>
public class TickerQDownloadQueue(/* ITimeTickerManager timeTickerManager */) : IDownloadQueue
{
    public Task EnqueueAsync(Guid downloadJobId, string streamUrl, CancellationToken ct = default)
    {
        // TODO: Implement with TickerQ
        return Task.CompletedTask;
    }

    public Task RequeueAsync(Guid downloadJobId, string streamUrl, CancellationToken ct = default)
    {
        // TODO: Implement with TickerQ
        return Task.CompletedTask;
    }
}
