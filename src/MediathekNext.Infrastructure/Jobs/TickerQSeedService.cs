using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
// using TickerQ.Utilities.Entities; // TODO: Add proper TickerQ reference
// using TickerQ.Utilities.Interfaces.Managers; // TODO: Add proper TickerQ reference

namespace MediathekNext.Infrastructure.Jobs;

/// <summary>
/// Seeds cron tickers on startup if they don't already exist.
/// Because TickerQ persists cron tickers in the database, this is idempotent —
/// existing tickers are left untouched (schedule changes survive app restarts).
///
/// To change a cron expression: edit it via the TickerQ dashboard or delete the
/// cron ticker row from the DB and let the seed re-create it.
/// </summary>
public class TickerQSeedService(
    /* ICronTickerManager cronTickerManager, */ // TODO: Implement TickerQ
    ILogger<TickerQSeedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        // TODO: Implement TickerQ integration
        logger.LogInformation("TickerQSeedService: TickerQ integration not yet implemented");
        await Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
