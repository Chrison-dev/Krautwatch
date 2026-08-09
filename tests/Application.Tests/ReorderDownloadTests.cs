using Krautwatch.Application.Downloads;
using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Enums;
using Krautwatch.Domain.Interfaces;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Krautwatch.Application.Tests;

/// <summary>
/// Covers the priority half of #51 — the motivating case being a manual grab buried behind a season
/// pack an RSS-Sync enqueued moments earlier.
/// </summary>
public class ReorderDownloadTests
{
    private readonly IDownloadJobRepository _jobs = Substitute.For<IDownloadJobRepository>();

    private static DownloadJob Job(int priority = 0, DateTimeOffset? created = null)
    {
        var job = new DownloadJob
        {
            EpisodeId = "ep", StreamUrl = "https://example.com/a.mp4",
            CreatedAt = created ?? DateTimeOffset.UtcNow,
        };
        if (priority != 0) job.SetPriority(priority);
        return job;
    }

    private void Queue(params DownloadJob[] queued)
    {
        foreach (var job in queued)
            _jobs.GetByIdAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);

        _jobs.GetQueuedOrderedAsync(Arg.Any<CancellationToken>())
            .Returns(queued.OrderByDescending(j => j.Priority).ThenBy(j => j.CreatedAt).ToList());
    }

    private Task<ReorderResult> Move(DownloadJob job, QueueMove move) =>
        new ReorderDownloadHandler(_jobs).HandleAsync(job.Id, move, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Moving_to_the_top_outranks_every_queued_job()
    {
        var first = Job(created: DateTimeOffset.UtcNow.AddMinutes(-10));
        var second = Job(created: DateTimeOffset.UtcNow.AddMinutes(-5));
        var mine = Job(created: DateTimeOffset.UtcNow);
        Queue(first, second, mine);

        (await Move(mine, QueueMove.ToTop)).Ok.ShouldBeTrue();

        mine.Priority.ShouldBeGreaterThan(first.Priority);
        mine.Priority.ShouldBeGreaterThan(second.Priority);
        await _jobs.Received(1).UpdateAsync(mine, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Moving_to_the_bottom_falls_behind_every_queued_job()
    {
        var mine = Job(created: DateTimeOffset.UtcNow.AddMinutes(-10));
        var other = Job(created: DateTimeOffset.UtcNow);
        Queue(mine, other);

        (await Move(mine, QueueMove.ToBottom)).Ok.ShouldBeTrue();

        mine.Priority.ShouldBeLessThan(other.Priority);
    }

    [Fact]
    public async Task Moving_to_the_top_twice_keeps_the_most_recent_on_top()
    {
        // Each move takes max+1, so the winner is whoever asked last — not whoever asked first.
        var a = Job();
        var b = Job();
        Queue(a, b);

        await Move(a, QueueMove.ToTop);
        Queue(a, b);
        await Move(b, QueueMove.ToTop);

        b.Priority.ShouldBeGreaterThan(a.Priority);
    }

    [Fact]
    public async Task A_download_that_is_no_longer_queued_is_refused_with_a_reason()
    {
        // Reachable in practice: the Activity page polls every 3s, so a job can start downloading
        // between the row rendering and the click landing. Silently doing nothing would look broken.
        var job = Job();
        job.MarkClaiming("worker-1");
        _jobs.GetByIdAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);

        var result = await Move(job, QueueMove.ToTop);

        result.Ok.ShouldBeFalse();
        result.Problem.ShouldNotBeNull().ShouldContain("Downloading");
        await _jobs.DidNotReceive().UpdateAsync(Arg.Any<DownloadJob>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_vanished_download_is_reported_rather_than_throwing()
    {
        _jobs.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((DownloadJob?)null);

        var result = await new ReorderDownloadHandler(_jobs)
            .HandleAsync(Guid.NewGuid(), QueueMove.ToTop, TestContext.Current.CancellationToken);

        result.Ok.ShouldBeFalse();
    }

    [Fact]
    public async Task Reordering_a_queue_of_one_writes_nothing()
    {
        var only = Job();
        Queue(only);

        (await Move(only, QueueMove.ToTop)).Ok.ShouldBeTrue();

        only.Priority.ShouldBe(0);
        await _jobs.DidNotReceive().UpdateAsync(Arg.Any<DownloadJob>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Setting_priority_on_a_non_queued_job_is_refused_at_the_entity()
    {
        // The handler guards this, but so does the entity — reordering a running download is meaningless
        // and the invariant belongs with the state it protects.
        var job = Job();
        job.MarkCompleted("/downloads/x.mp4", 1);

        Should.Throw<InvalidOperationException>(() => job.SetPriority(5));
    }

    // ── SABnzbd mapping ───────────────────────────────────────

    [Theory]
    [InlineData("-2", -1)]   // paused collapses onto Low: we have no paused state to honour
    [InlineData("-1", -1)]
    [InlineData("0", 0)]
    [InlineData("1", 1)]
    [InlineData("2", 2)]
    [InlineData("99", 2)]    // clamped rather than trusted
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("garbage", 0)]
    public void Sab_priority_maps_onto_ours(string? sab, int expected) =>
        SabnzbdPriority.ToJobPriority(sab).ShouldBe(expected);

    [Theory]
    [InlineData(-5, "Low")]
    [InlineData(-1, "Low")]
    [InlineData(0, "Normal")]
    [InlineData(1, "High")]
    [InlineData(7, "Force")]
    public void Job_priority_maps_back_to_a_sab_name(int priority, string expected) =>
        SabnzbdPriority.ToDisplayName(priority).ShouldBe(expected);
}
