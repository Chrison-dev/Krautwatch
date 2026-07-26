using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Enums;
using Krautwatch.Infrastructure.Downloads;
using Krautwatch.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace Krautwatch.Infrastructure.Tests;

public class DownloadJobRepositoryTests : IDisposable
{
    // Keep one open in-memory connection; use a FRESH DbContext per operation, like the worker's
    // per-poll scopes — otherwise ExecuteUpdate + a re-read on the same context returns a stale
    // tracked entity.
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<AppDbContext> _options;

    public DownloadJobRepositoryTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conn).Options;

        using var db = new AppDbContext(_options);
        db.Database.EnsureCreated();
        db.Channels.Add(new Channel { Id = "zdf", Name = "ZDF", ProviderKey = "zdf" });
        db.Shows.Add(new Show { Id = "zdf:heute-show", Title = "heute-show", ChannelId = "zdf" });
        db.Episodes.Add(new Episode
        {
            Id = "zdf:1", Title = "ep", ShowId = "zdf:heute-show",
            BroadcastDate = DateTimeOffset.UtcNow, Duration = TimeSpan.FromMinutes(30),
        });
        db.SaveChanges();
    }

    private DownloadJobRepository Repo() => new(new AppDbContext(_options));

    private async Task<Guid> AddQueuedAsync(DateTimeOffset createdAt)
    {
        await using var db = new AppDbContext(_options);
        var job = new DownloadJob
        {
            Id = Guid.NewGuid(),
            EpisodeId = "zdf:1",
            StreamUrl = "https://cdn/x.mp4",
            Quality = VideoQuality.High,
            CreatedAt = createdAt,
        };
        db.DownloadJobs.Add(job);
        await db.SaveChangesAsync();
        return job.Id;
    }

    [Fact]
    public async Task TryClaimNext_claims_the_oldest_queued_job_with_its_episode()
    {
        var older = await AddQueuedAsync(DateTimeOffset.UtcNow.AddMinutes(-10));
        await AddQueuedAsync(DateTimeOffset.UtcNow.AddMinutes(-1));

        var claimed = await Repo().TryClaimNextAsync("worker-1");

        claimed.ShouldNotBeNull();
        claimed!.Id.ShouldBe(older);
        claimed.Status.ShouldBe(DownloadStatus.Downloading);
        claimed.WorkerId.ShouldBe("worker-1");
        claimed.Episode.ShouldNotBeNull(); // included so the provider can name the file
    }

    [Fact]
    public async Task TryClaimNext_returns_null_when_nothing_is_queued()
    {
        (await Repo().TryClaimNextAsync("worker-1")).ShouldBeNull();
    }

    [Fact]
    public async Task TryClaimNext_will_not_hand_the_same_job_to_two_workers()
    {
        await AddQueuedAsync(DateTimeOffset.UtcNow.AddMinutes(-5));

        (await Repo().TryClaimNextAsync("worker-1")).ShouldNotBeNull();
        (await Repo().TryClaimNextAsync("worker-2")).ShouldBeNull(); // the only job is already claimed
    }

    [Fact]
    public async Task ReclaimStale_requeues_this_workers_downloading_jobs()
    {
        var id = await AddQueuedAsync(DateTimeOffset.UtcNow.AddMinutes(-5));
        await Repo().TryClaimNextAsync("worker-1"); // → Downloading, worker-1

        var reclaimed = await Repo().ReclaimStaleAsync("worker-1");

        reclaimed.ShouldBe(1);
        var job = await Repo().GetByIdAsync(id);
        job!.Status.ShouldBe(DownloadStatus.Queued);
        job.WorkerId.ShouldBeNull();
    }

    [Fact]
    public async Task ReclaimStale_leaves_other_workers_jobs_alone()
    {
        await AddQueuedAsync(DateTimeOffset.UtcNow.AddMinutes(-5));
        await Repo().TryClaimNextAsync("worker-1");

        (await Repo().ReclaimStaleAsync("worker-2")).ShouldBe(0);
    }

    public void Dispose() => _conn.Dispose();
}
