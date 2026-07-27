using System.Collections.Concurrent;
using System.Threading.Channels;
using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Enums;
using Krautwatch.Domain.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Krautwatch.Application.Indexing;

// ══════════════════════════════════════════════════════════════
// Options
// ══════════════════════════════════════════════════════════════

/// <summary>
/// Tuning for query-driven search (#58 / DR-011). Bound from <c>Indexing:OnDemandResolution</c>.
/// </summary>
public class OnDemandResolutionOptions
{
    public const string SectionName = "Indexing:OnDemandResolution";

    /// <summary>Kill switch — when false, search only ever reads what a crawler already persisted.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Budget for the background crawl, independent of any request. The ARD path is multi-hop
    /// (A-Z widget → show page → episode list → item page), so this is generous by design. Also the ceiling
    /// on how long a search can wait in <see cref="SearchWaitMode.WaitForComplete"/> mode — no wait is ever
    /// unbounded.
    /// </summary>
    public TimeSpan CrawlTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>How long a successful resolution is trusted before re-crawling.</summary>
    public TimeSpan PositiveTtl { get; set; } = TimeSpan.FromHours(6);

    /// <summary>
    /// How long an empty result is trusted. Shorter than <see cref="PositiveTtl"/>, but the more important
    /// of the two: Sonarr re-issues the same failing query every RSS-Sync cycle.
    /// </summary>
    public TimeSpan NegativeTtl { get; set; } = TimeSpan.FromMinutes(45);

    /// <summary>Politeness cap, so a Sonarr library refresh cannot become a crawl storm against ARD.</summary>
    public int MaxConcurrentResolutions { get; set; } = 2;
}

// ══════════════════════════════════════════════════════════════
// Action (IO-driven, DR-009) — runs in the API host; see the plan
// ══════════════════════════════════════════════════════════════

/// <summary>
/// Resolves a search term against the broadcasters on demand, so Sonarr can find a show no crawler has
/// visited yet (#58).
/// </summary>
/// <remarks>
/// <para>
/// <b>The wait is bounded; the crawl is not.</b> A caller waits at most
/// <see cref="OnDemandResolutionOptions.RequestDeadline"/> and is then released to serve whatever landed,
/// while the crawl runs to completion in the background so the next call gets the full set. Abandoning a
/// half-finished crawl would discard ARD round-trips already paid for and leave the cache permanently
/// partial.
/// </para>
/// <para>
/// Consequently the crawl <b>must not</b> observe the request's CancellationToken — it is queued here and
/// drained by <see cref="OnDemandResolutionService"/> under the host lifetime. Threading the request token
/// through would cancel every crawl the instant the HTTP response was written, which presents as
/// "resolution mysteriously never works".
/// </para>
/// <para>
/// Identical concurrent queries are coalesced: a second caller waits on the first one's completion instead
/// of starting a duplicate crawl. Per-process only — several API replicas would each crawl once, which is
/// acceptable and not worth distributed locking.
/// </para>
/// <para>
/// Singleton, so it deliberately takes <see cref="IServiceScopeFactory"/> rather than a repository: holding
/// a scoped repository (and its DbContext) for the process lifetime would be a captive dependency.
/// </para>
/// </remarks>
public sealed class OnDemandResolver(
    IServiceScopeFactory scopeFactory,
    OnDemandResolutionOptions options,
    ILogger<OnDemandResolver> logger)
{
    private readonly ConcurrentDictionary<string, Task> _inFlight = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _pending = new(StringComparer.Ordinal);
    // Fully qualified: Domain has its own Channel entity (a broadcaster channel).
    private readonly System.Threading.Channels.Channel<string> _queue =
        System.Threading.Channels.Channel.CreateUnbounded<string>();

    internal ChannelReader<string> Queue => _queue.Reader;

    /// <summary>
    /// Ensures the term has been (or is being) resolved, waiting as long as the operator has asked for.
    /// Returns true when a resolution completed inside that window, so the caller knows a re-read is
    /// worthwhile.
    /// </summary>
    public async Task<bool> EnsureResolvedAsync(string query, CancellationToken ct = default)
    {
        if (!options.Enabled || string.IsNullOrWhiteSpace(query))
            return false;

        var normalised = ResolvedQuery.Normalise(query);
        if (normalised.Length == 0)
            return false;

        if (await IsFreshAsync(normalised, ct))
            return false; // looked recently — nothing to wait for

        // Join an in-flight resolution, or start one. GetOrAdd keeps it to a single crawl per term.
        var resolution = _inFlight.GetOrAdd(normalised, key =>
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[key] = completion;
            _queue.Writer.TryWrite(key); // unbounded, so this always succeeds
            return completion.Task;
        });

        // How long to wait is the operator's choice (AppSettings), not a compiled-in constant.
        var wait = await ResolveWaitAsync(ct);

        var finished = await Task.WhenAny(resolution, Task.Delay(wait, ct));
        if (finished == resolution)
            return true;

        logger.LogDebug(
            "Resolution of '{Query}' still running after {Wait}; serving what is available and letting the "
            + "crawl finish in the background.", normalised, wait);
        return false;
    }

    /// <summary>
    /// Releases waiters and clears the in-flight entry so the term can be resolved again once its TTL
    /// lapses. One method rather than two, so a resolution can never signal without releasing (leaking the
    /// term forever) or release without signalling (stranding a caller until its deadline).
    /// </summary>
    internal void ReleaseAndSignal(string key)
    {
        _inFlight.TryRemove(key, out _);
        if (_pending.TryRemove(key, out var completion))
            completion.TrySetResult();
    }

    /// <summary>
    /// How long to wait for a resolution, per the operator's preference.
    /// <see cref="SearchWaitMode.WaitForComplete"/> waits up to the crawl's own ceiling — never
    /// indefinitely, since a stuck crawl would otherwise hang the request forever.
    /// </summary>
    private async Task<TimeSpan> ResolveWaitAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var settings = await scope.ServiceProvider
                .GetRequiredService<ISettingsRepository>().GetAsync(ct);

            return settings.SearchWaitMode == SearchWaitMode.WaitForComplete
                ? options.CrawlTimeout
                : TimeSpan.FromSeconds(Math.Clamp(settings.SearchWaitSeconds, 1, 300));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never fail a search because the preference could not be read — fall back to the safe default.
            logger.LogWarning(ex, "Could not read the search wait preference; defaulting to 8s.");
            return TimeSpan.FromSeconds(8);
        }
    }

    private async Task<bool> IsFreshAsync(string normalised, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var previous = await scope.ServiceProvider
            .GetRequiredService<IResolvedQueryRepository>()
            .GetAsync(normalised, ct);

        if (previous is null)
            return false;

        var ttl = previous.ResultCount > 0 ? options.PositiveTtl : options.NegativeTtl;
        return DateTimeOffset.UtcNow - previous.LastAttemptedAt < ttl;
    }
}

/// <summary>
/// Drains the resolution queue, crawling each term against every registered broadcaster and persisting what
/// comes back. Runs under the host lifetime, so a crawl survives the HTTP response that triggered it.
/// </summary>
public sealed class OnDemandResolutionService(
    OnDemandResolver resolver,
    IServiceScopeFactory scopeFactory,
    OnDemandResolutionOptions options,
    ILogger<OnDemandResolutionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var gate = new SemaphoreSlim(Math.Max(1, options.MaxConcurrentResolutions));

        try
        {
            await foreach (var query in resolver.Queue.ReadAllAsync(stoppingToken))
            {
                await gate.WaitAsync(stoppingToken);

                // Deliberately not awaited: one slow crawl must not block the queue. The semaphore bounds
                // concurrency; stoppingToken — never a request token — bounds lifetime.
                _ = ResolveAsync(query, stoppingToken)
                    .ContinueWith(_ => gate.Release(), TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // normal shutdown
        }
    }

    private async Task ResolveAsync(string normalised, CancellationToken stoppingToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(options.CrawlTimeout);

        var persisted = 0;
        var providers = Array.Empty<string>();

        try
        {
            using var scope = scopeFactory.CreateScope();
            var crawlers = scope.ServiceProvider.GetServices<IBroadcasterCrawler>().ToList();
            var episodes = scope.ServiceProvider.GetRequiredService<IEpisodeRepository>();

            // Captured before the fan-out: building this inside the concurrent lambdas would be a
            // data race on a non-thread-safe collection.
            providers = crawlers.Select(c => c.ProviderKey).ToArray();

            // We cannot know which broadcaster carries a title, so ask all of them.
            var results = await Task.WhenAll(crawlers.Select(async crawler =>
            {
                try
                {
                    return await crawler.CrawlShowAsync(normalised, timeout.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One broadcaster failing must not sink the others.
                    logger.LogWarning(ex, "Crawler {Provider} failed resolving '{Query}'.",
                        crawler.ProviderKey, normalised);
                    return [];
                }
            }));

            var found = results.SelectMany(r => r).ToList();
            if (found.Count > 0)
            {
                await episodes.UpsertManyAsync(found, timeout.Token);
                persisted = found.Count;
            }

            logger.LogInformation("Resolved '{Query}' on demand: {Count} episode(s) from {Providers}.",
                normalised, persisted, string.Join(", ", providers));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Don't record an attempt that never ran, or shutdown would poison the cache with a false miss.
            logger.LogDebug("Resolution of '{Query}' abandoned — host is shutting down.", normalised);
            resolver.ReleaseAndSignal(normalised);
            return;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Resolution of '{Query}' failed.", normalised);
        }

        await RecordAttemptAsync(normalised, persisted, providers, stoppingToken);

        // Signalled last, so a caller still inside its deadline sees the persisted episodes.
        resolver.ReleaseAndSignal(normalised);
    }

    private async Task RecordAttemptAsync(
        string normalised, int persisted, string[] providers, CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IResolvedQueryRepository>().RecordAsync(
                new ResolvedQuery
                {
                    Query = normalised,
                    LastAttemptedAt = DateTimeOffset.UtcNow,
                    ResultCount = persisted,
                    ProvidersTried = providers.Length > 0 ? string.Join(",", providers) : null,
                },
                stoppingToken);
        }
        catch (Exception ex)
        {
            // Failing to record only costs a redundant crawl later — never fail the resolution over it.
            logger.LogWarning(ex, "Could not record the resolution attempt for '{Query}'.", normalised);
        }
    }
}
