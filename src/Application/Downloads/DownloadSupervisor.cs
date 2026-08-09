using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Krautwatch.Application.Downloads;

/// <summary>
/// Drives downloads off the durable job table: reclaims anything this worker left mid-flight on a
/// previous crash, then keeps up to <c>MaxConcurrentDownloads</c> jobs running at once, claiming each
/// atomically so multiple instances stay safe. The <see cref="DownloadJob"/> row is the work queue — no
/// messaging bus needed.
/// </summary>
/// <remarks>
/// <para>
/// The limit is <b>per process</b> (#51). A global cap would mean counting live claims across processes
/// and coordinating on every claim, which is real complexity for a case that does not exist yet — the
/// shipped compose runs exactly one downloader, so per-process and global are the same number today.
/// Scaling the agent to two replicas doubles the effective limit, which is why the settings page says so.
/// </para>
/// <para>
/// The limit is re-read on every pass rather than captured at startup, because it is editable in the UI
/// and an operator who lowers it expects that to take effect. Polling the row is far cheaper than
/// threading an invalidation channel from Application down into an agent.
/// </para>
/// </remarks>
public sealed class DownloadSupervisor(IServiceScopeFactory scopeFactory, ILogger<DownloadSupervisor> logger)
    : BackgroundService
{
    /// <summary>
    /// Identifies the <b>process</b>, not an individual runner. Startup reclaim keys off this, so making
    /// it per-runner would orphan rows whenever the concurrency limit changed between restarts.
    /// </summary>
    internal static readonly string WorkerId = $"downloader-{Environment.MachineName}";

    /// <summary>How long to wait before looking again when nothing is queued and nothing is running.</summary>
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How often to re-check while downloads are in flight. Short, because it is what makes an edited
    /// concurrency limit and a newly queued job take effect promptly — a single-row read at this rate is
    /// negligible next to the download it runs alongside.
    /// </summary>
    private static readonly TimeSpan BusyPollInterval = TimeSpan.FromSeconds(2);

    /// <summary>Fallback when the settings row cannot be read — keeps downloads moving, one at a time.</summary>
    internal const int DefaultConcurrency = 1;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ReclaimStaleAsync(stoppingToken);
        logger.LogInformation("Download worker {WorkerId} started.", WorkerId);

        var inFlight = new List<Task>();
        var lastLimit = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var limit = await ReadConcurrencyLimitAsync(stoppingToken);
                if (limit != lastLimit)
                {
                    logger.LogInformation("Download concurrency limit is {Limit} (per process).", limit);
                    lastLimit = limit;
                }

                // Reap first, so a lowered limit takes effect against an accurate count and finished
                // runs never hold a slot. RunAsync swallows its own failures, so nothing faults here.
                inFlight.RemoveAll(t => t.IsCompleted);

                // Fill the free slots. Stops early when the queue runs dry, so an empty queue costs one
                // claim attempt per pass rather than one per slot.
                var claimedAny = false;
                while (inFlight.Count < limit && !stoppingToken.IsCancellationRequested)
                {
                    var job = await ClaimNextAsync(stoppingToken);
                    if (job is null) break;

                    claimedAny = true;
                    inFlight.Add(RunAsync(job.Id, stoppingToken));
                }

                if (claimedAny && inFlight.Count < limit)
                    continue;   // took work and still have room — go straight back for more

                if (inFlight.Count == 0)
                {
                    // Idle. Nothing to wait on but the clock.
                    await Task.Delay(IdleDelay, stoppingToken);
                }
                else
                {
                    // Something is running. Wake on whichever comes first: a run finishing, or the poll
                    // interval. The timeout is what makes a raised limit take effect and a newly queued
                    // job get picked up — waiting only on the runs would mean neither could be noticed
                    // until a download ended, which for a long download is effectively never.
                    await Task.WhenAny(Task.WhenAny(inFlight), Task.Delay(BusyPollInterval, stoppingToken));
                }
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

        // Let whatever is mid-download finish its own cancellation path, so the runners get to record
        // state rather than being abandoned. Anything still Downloading is reclaimed on next startup.
        if (inFlight.Count > 0)
        {
            logger.LogInformation("Waiting for {Count} in-flight download(s) to stop.", inFlight.Count);
            try { await Task.WhenAll(inFlight); } catch { /* each run logs its own failure */ }
        }

        logger.LogInformation("Download worker stopping.");
    }

    /// <summary>
    /// Runs one job in its own DI scope. The scope is per-run and never shared: <c>DbContext</c> and the
    /// scoped repositories are not thread-safe, so reusing one across concurrent runs is a data race.
    /// </summary>
    private async Task RunAsync(Guid jobId, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var jobs = scope.ServiceProvider.GetRequiredService<IDownloadJobRepository>();

            // Re-read inside this scope so the entity is tracked by the context that will write it.
            var job = await jobs.GetByIdAsync(jobId, ct);
            if (job is null)
            {
                logger.LogWarning("Claimed download {JobId} vanished before it could run.", jobId);
                return;
            }

            await scope.ServiceProvider.GetRequiredService<RunDownloadHandler>().HandleAsync(job, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down; the row stays Downloading and is reclaimed on next startup.
        }
        catch (Exception ex)
        {
            // Contained deliberately: one bad download must not take down the supervisor or its siblings.
            logger.LogError(ex, "Download {JobId} failed.", jobId);
        }
    }

    private async Task<DownloadJob?> ClaimNextAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IDownloadJobRepository>();
        return await jobs.TryClaimNextAsync(WorkerId, ct);
    }

    /// <summary>
    /// The operator's <c>MaxConcurrentDownloads</c>, clamped to at least 1 — a stored 0 would otherwise
    /// stall the queue with no visible cause, and validation already keeps the UI from producing one.
    /// </summary>
    private async Task<int> ReadConcurrencyLimitAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();
            return Math.Max(1, (await settings.GetAsync(ct)).MaxConcurrentDownloads);
        }
        catch (Exception ex)
        {
            // A database blip must not stop downloads entirely; fall back to sequential.
            logger.LogWarning(ex, "Could not read the concurrency limit; using {Default}.", DefaultConcurrency);
            return DefaultConcurrency;
        }
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
