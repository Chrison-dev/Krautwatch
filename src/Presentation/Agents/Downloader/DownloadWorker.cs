using Krautwatch.Application.Downloads;
using Krautwatch.Domain.Interfaces;

namespace Krautwatch.Agents.Downloader;

/// <summary>
/// Drives downloads off the durable job table: reclaims anything this worker left mid-flight on a
/// previous crash, then repeatedly claims the next Queued job (atomically, so multiple instances are
/// safe) and runs it. The <see cref="DownloadJob"/> row is the work queue — no messaging bus needed.
/// </summary>
public sealed class DownloadWorker(IServiceScopeFactory scopeFactory, ILogger<DownloadWorker> logger)
    : BackgroundService
{
    internal static readonly string WorkerId = $"downloader-{Environment.MachineName}";
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ReclaimStaleAsync(stoppingToken);
        logger.LogInformation("Download worker {WorkerId} started.", WorkerId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var jobs = scope.ServiceProvider.GetRequiredService<IDownloadJobRepository>();

                var job = await jobs.TryClaimNextAsync(WorkerId, stoppingToken);
                if (job is null)
                {
                    await Task.Delay(IdleDelay, stoppingToken);
                    continue;
                }

                var runner = scope.ServiceProvider.GetRequiredService<RunDownloadHandler>();
                await runner.HandleAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Download worker loop error — backing off.");
                try { await Task.Delay(IdleDelay, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        logger.LogInformation("Download worker stopping.");
    }

    private async Task ReclaimStaleAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var jobs = scope.ServiceProvider.GetRequiredService<IDownloadJobRepository>();
            var reclaimed = await jobs.ReclaimStaleAsync(WorkerId, ct);
            if (reclaimed > 0)
                logger.LogInformation("Reclaimed {Count} stale download(s) from a previous run.", reclaimed);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Stale-download reclaim failed.");
        }
    }
}
