using Krautwatch.Application.Downloads;
using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Enums;
using Krautwatch.Domain.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Krautwatch.Application.Tests;

/// <summary>
/// Covers the concurrency half of #51. Before this, <c>MaxConcurrentDownloads</c> was persisted,
/// validated and editable — and read by nothing, so downloads always ran one at a time whatever the
/// setting said.
/// </summary>
public class DownloadSupervisorTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    // ── harness ───────────────────────────────────────────────

    /// <summary>Blocks every download until released, so concurrency is observable.</summary>
    private sealed class GatedProvider : IDownloadProvider
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _gate = new();
        private readonly List<Guid> _started = [];

        public HashSet<Guid> FailFor { get; } = [];
        public int Running { get; private set; }
        public int PeakConcurrent { get; private set; }

        public IReadOnlyList<Guid> Started
        {
            get { lock (_gate) return _started.ToList(); }
        }

        public void ReleaseAll() => _release.TrySetResult();

        public async Task<DownloadResult> DownloadAsync(
            DownloadJob job, string outputDirectory, IProgress<double> progress, CancellationToken ct = default)
        {
            lock (_gate)
            {
                _started.Add(job.Id);
                Running++;
                PeakConcurrent = Math.Max(PeakConcurrent, Running);
            }

            try
            {
                await _release.Task.WaitAsync(ct);

                if (FailFor.Contains(job.Id))
                    throw new InvalidOperationException("boom");

                return new DownloadResult($"{outputDirectory}/{job.Id}.mp4", 1024);
            }
            finally
            {
                lock (_gate) Running--;
            }
        }
    }

    /// <summary>
    /// In-memory stand-in for the job table. The claim is done under a lock, mirroring the real
    /// conditional UPDATE: at most one caller may win a given job.
    /// </summary>
    private sealed class FakeJobs : IDownloadJobRepository
    {
        private readonly object _gate = new();
        private readonly List<DownloadJob> _jobs = [];

        public void Seed(params DownloadJob[] jobs) { lock (_gate) _jobs.AddRange(jobs); }

        public Task<DownloadJob?> TryClaimNextAsync(string workerId, CancellationToken ct = default)
        {
            lock (_gate)
            {
                var next = _jobs.FirstOrDefault(j => j.Status == DownloadStatus.Queued);
                if (next is null) return Task.FromResult<DownloadJob?>(null);

                next.MarkClaiming(workerId);
                return Task.FromResult<DownloadJob?>(next);
            }
        }

        public Task<DownloadJob?> GetByIdAsync(Guid id, CancellationToken ct = default)
        {
            lock (_gate) return Task.FromResult(_jobs.FirstOrDefault(j => j.Id == id));
        }

        public Task<DownloadStatus?> GetStatusAsync(Guid id, CancellationToken ct = default)
        {
            lock (_gate) return Task.FromResult(_jobs.FirstOrDefault(j => j.Id == id)?.Status);
        }

        public Task<int> ReclaimStaleAsync(string workerId, CancellationToken ct = default) => Task.FromResult(0);
        public Task UpdateAsync(DownloadJob job, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateProgressAsync(Guid id, double percent, CancellationToken ct = default) => Task.CompletedTask;
        public Task AddAsync(DownloadJob job, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<DownloadJob>> GetAllAsync(CancellationToken ct = default)
        {
            lock (_gate) return Task.FromResult<IReadOnlyList<DownloadJob>>(_jobs.ToList());
        }

        public Task<IReadOnlyList<DownloadJob>> GetByStatusAsync(DownloadStatus status, CancellationToken ct = default)
        {
            lock (_gate) return Task.FromResult<IReadOnlyList<DownloadJob>>(
                _jobs.Where(j => j.Status == status).ToList());
        }

        public Task<IReadOnlyList<DownloadJob>> GetByWorkerIdAsync(string workerId, CancellationToken ct = default)
        {
            lock (_gate) return Task.FromResult<IReadOnlyList<DownloadJob>>(
                _jobs.Where(j => j.WorkerId == workerId).ToList());
        }
    }

    /// <summary>Settings whose concurrency limit can be changed mid-run, as the UI can.</summary>
    private sealed class MutableSettings : ISettingsRepository
    {
        private readonly AppSettings _settings = new()
        {
            Id = 1, DownloadDirectory = "/tmp/krautwatch-tests", MaxConcurrentDownloads = 1,
        };

        public int Limit
        {
            get => _settings.MaxConcurrentDownloads;
            set => _settings.MaxConcurrentDownloads = value;
        }

        public Task<AppSettings> GetAsync(CancellationToken ct = default) => Task.FromResult(_settings);
        public Task SaveAsync(AppSettings settings, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class Harness : IAsyncDisposable
    {
        public readonly FakeJobs Jobs = new();
        public readonly GatedProvider Provider = new();
        public readonly MutableSettings Settings = new();
        public readonly DownloadSupervisor Supervisor;

        public Harness(int limit, int queuedJobs)
        {
            Settings.Limit = limit;
            Jobs.Seed(Enumerable.Range(0, queuedJobs).Select(_ => NewJob()).ToArray());

            var services = new ServiceCollection();
            services.AddSingleton<IDownloadJobRepository>(Jobs);
            services.AddSingleton<IDownloadProvider>(Provider);
            services.AddSingleton<ISettingsRepository>(Settings);
            // Registered against ILogger<T>, not the concrete NullLogger<T> — otherwise
            // ILogger<RunDownloadHandler> stays unresolvable and every run dies before reaching the
            // provider, invisibly, because the supervisor contains its failures by design.
            services.AddSingleton<ILogger<RunDownloadHandler>>(NullLogger<RunDownloadHandler>.Instance);
            services.AddScoped<RunDownloadHandler>();

            var provider = services.BuildServiceProvider();

            Supervisor = new DownloadSupervisor(
                provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<DownloadSupervisor>.Instance);
        }

        public Task StartAsync() => Supervisor.StartAsync(CancellationToken.None);

        public async ValueTask DisposeAsync()
        {
            Provider.ReleaseAll();
            try { await Supervisor.StopAsync(CancellationToken.None).WaitAsync(Patience); }
            catch (TimeoutException) { /* shutting down a stuck test */ }
            Supervisor.Dispose();
        }
    }

    private static DownloadJob NewJob() => new()
    {
        EpisodeId = "ep-1",
        StreamUrl = "https://example.com/a.mp4",
        Episode = new Episode
        {
            Id = "ep-1", ShowId = "show-1", Title = "Test",
        },
    };

    /// <summary>Polls until <paramref name="condition"/> holds, so tests never depend on a fixed sleep.</summary>
    private static async Task WaitUntil(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }

        throw new Xunit.Sdk.XunitException($"Timed out waiting: {because}");
    }

    // ── tests ─────────────────────────────────────────────────

    [Fact]
    public async Task Runs_up_to_the_configured_limit_at_once()
    {
        await using var h = new Harness(limit: 3, queuedJobs: 6);

        await h.StartAsync();
        await WaitUntil(() => h.Provider.Running == 3, "three downloads to be running");

        // Hold it there: the fourth must not start while three are still in flight.
        await Task.Delay(200);
        h.Provider.Running.ShouldBe(3);
        h.Provider.PeakConcurrent.ShouldBe(3);
    }

    [Fact]
    public async Task A_limit_of_one_stays_sequential()
    {
        // The old behaviour, which must remain available — and is what an operator who leaves the
        // setting alone on an upgrade gets.
        await using var h = new Harness(limit: 1, queuedJobs: 4);

        await h.StartAsync();
        await WaitUntil(() => h.Provider.Running == 1, "one download to be running");

        await Task.Delay(200);
        h.Provider.PeakConcurrent.ShouldBe(1);
    }

    [Fact]
    public async Task Picks_up_a_raised_limit_without_a_restart()
    {
        // #51 asks for this explicitly: the setting is editable in the UI, so capturing it at startup
        // would mean an operator's change silently did nothing until they restarted the container.
        await using var h = new Harness(limit: 1, queuedJobs: 5);

        await h.StartAsync();
        await WaitUntil(() => h.Provider.Running == 1, "the first download to be running");

        h.Settings.Limit = 3;

        await WaitUntil(() => h.Provider.Running == 3, "the raised limit to take effect");
    }

    [Fact]
    public async Task One_failing_download_does_not_stop_the_others()
    {
        await using var h = new Harness(limit: 3, queuedJobs: 3);

        await h.StartAsync();
        await WaitUntil(() => h.Provider.Running == 3, "all three to be running");

        // Fail exactly one of the in-flight jobs, then let everything complete.
        h.Provider.FailFor.Add(h.Provider.Started[0]);
        h.Provider.ReleaseAll();

        // The supervisor must survive and drain the queue rather than dying with the failed job.
        await WaitUntil(() => h.Provider.Running == 0, "all downloads to finish");
        h.Provider.Started.Count.ShouldBe(3);
    }

    [Fact]
    public async Task An_empty_queue_does_not_spin()
    {
        await using var h = new Harness(limit: 3, queuedJobs: 0);

        await h.StartAsync();
        await Task.Delay(300);

        h.Provider.Started.ShouldBeEmpty();
    }
}
