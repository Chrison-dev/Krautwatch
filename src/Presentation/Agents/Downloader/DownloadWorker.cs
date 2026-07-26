using Krautwatch.Application.Downloads;
using Krautwatch.Domain.Interfaces;

namespace Krautwatch.Agents.Downloader;

/// <summary>
/// Polls the durable job table for the next Queued download and runs it. The <see cref="DownloadJob"/>
/// row is the work queue (its phase-transition + WorkerId design is built for a claiming worker), so
/// no cross-process bus is needed — a scale-out later can move to a Postgres/RabbitMQ transport.
/// </summary>
public sealed class DownloadWorker(IServiceScopeFactory scopeFactory, ILogger<DownloadWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Download worker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var jobs = scope.ServiceProvider.GetRequiredService<IDownloadJobRepository>();

                var next = await jobs.GetNextQueuedAsync(stoppingToken);
                if (next is null)
                {
                    await Task.Delay(IdleDelay, stoppingToken);
                    continue;
                }

                var runner = scope.ServiceProvider.GetRequiredService<RunDownloadHandler>();
                await runner.HandleAsync(next.Id, stoppingToken);
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
}
