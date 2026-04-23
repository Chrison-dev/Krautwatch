using MediathekNext.Application.Downloads;
using MediathekNext.Domain.Entities;
using MediathekNext.Domain.Enums;
using MediathekNext.Domain.Interfaces;
using NSubstitute;
using Shouldly;
using Xunit;

namespace MediathekNext.Application.Tests;

// ──────────────────────────────────────────────────────────────
// Shared fixtures
// ──────────────────────────────────────────────────────────────

file static class Fixtures
{
    public static Episode MakeEpisode(string streamId = "stream-1") => new()
    {
        Id      = "ep-1",
        Title   = "Tagesschau 20 Uhr",
        ShowId  = "show-1",
        Show = new Show
        {
            Id = "show-1", Title = "Tagesschau", ChannelId = "ard",
            Channel = new Channel { Id = "ard", Name = "ARD", ProviderKey = "mediathekview" }
        },
        BroadcastDate = DateTimeOffset.UtcNow,
        Duration      = TimeSpan.FromMinutes(15),
        Streams =
        [
            new EpisodeStream
            {
                Id        = streamId,
                EpisodeId = "ep-1",
                Quality   = VideoQuality.Standard,
                Url       = "https://example.com/ep.mp4",
                Format    = "mp4"
            }
        ]
    };

    public static DownloadJob MakeJob(
        DownloadStatus status = DownloadStatus.Queued,
        Guid? id = null) =>
        new()
        {
            Id        = id ?? Guid.NewGuid(),
            EpisodeId = "ep-1",
            StreamUrl = "https://example.com/ep.mp4",
            Quality   = VideoQuality.Standard,
        };
}

// ──────────────────────────────────────────────────────────────
// StartDownloadHandler
// ──────────────────────────────────────────────────────────────

public class StartDownloadHandlerTests
{
    private readonly IEpisodeRepository    _episodes = Substitute.For<IEpisodeRepository>();
    private readonly IDownloadJobRepository _jobs    = Substitute.For<IDownloadJobRepository>();
    private readonly IDownloadQueue        _queue    = Substitute.For<IDownloadQueue>();

    private StartDownloadHandler Handler() => new(_episodes, _jobs, _queue);

    [Fact]
    public async Task ValidRequest_CreatesJobAndEnqueues()
    {
        var episode = Fixtures.MakeEpisode();
        _episodes.GetByIdAsync("ep-1", default).Returns(episode);

        var result = await Handler().HandleAsync(new StartDownloadRequest("ep-1", "stream-1"));

        result.ShouldNotBeNull();
        result!.EpisodeId.ShouldBe("ep-1");
        result.Status.ShouldBe(nameof(DownloadStatus.Queued));

        await _jobs.Received(1).AddAsync(Arg.Any<DownloadJob>(), default);
        await _queue.Received(1).EnqueueAsync(
            Arg.Any<Guid>(),
            "https://example.com/ep.mp4",
            default);
    }

    [Fact]
    public async Task EpisodeNotFound_ReturnsNull_NoJobCreated()
    {
        _episodes.GetByIdAsync("bad-id", default).Returns((Episode?)null);

        var result = await Handler().HandleAsync(new StartDownloadRequest("bad-id", "stream-1"));

        result.ShouldBeNull();
        await _jobs.DidNotReceive().AddAsync(Arg.Any<DownloadJob>(), default);
        await _queue.DidNotReceive().EnqueueAsync(Arg.Any<Guid>(), Arg.Any<string>(), default);
    }

    [Fact]
    public async Task StreamNotFound_ReturnsNull_NoJobCreated()
    {
        var episode = Fixtures.MakeEpisode("real-stream");
        _episodes.GetByIdAsync("ep-1", default).Returns(episode);

        var result = await Handler().HandleAsync(new StartDownloadRequest("ep-1", "wrong-stream"));

        result.ShouldBeNull();
        await _jobs.DidNotReceive().AddAsync(Arg.Any<DownloadJob>(), default);
    }
}

// ──────────────────────────────────────────────────────────────
// CancelDownloadHandler
// ──────────────────────────────────────────────────────────────

public class CancelDownloadHandlerTests
{
    private readonly IDownloadJobRepository _jobs = Substitute.For<IDownloadJobRepository>();
    private CancelDownloadHandler Handler() => new(_jobs);

    [Theory]
    [InlineData(DownloadStatus.Queued)]
    [InlineData(DownloadStatus.Resolving)]
    [InlineData(DownloadStatus.Downloading)]
    [InlineData(DownloadStatus.Finalising)]
    public async Task ActiveStatus_CancelsAndReturnsTrue(DownloadStatus status)
    {
        var job = Fixtures.MakeJob();
        ApplyStatus(job, status);
        _jobs.GetByIdAsync(job.Id, default).Returns(job);

        var result = await Handler().HandleAsync(job.Id);

        result.ShouldBeTrue();
        job.Status.ShouldBe(DownloadStatus.Cancelled);
        await _jobs.Received(1).UpdateAsync(job, default);
    }

    [Theory]
    [InlineData(DownloadStatus.Completed)]
    [InlineData(DownloadStatus.Cancelled)]
    [InlineData(DownloadStatus.UrlUnavailable)]
    [InlineData(DownloadStatus.DownloadFailed)]
    [InlineData(DownloadStatus.FinaliseFailed)]
    public async Task TerminalStatus_ReturnsFalse_NoUpdate(DownloadStatus status)
    {
        var job = Fixtures.MakeJob();
        ApplyStatus(job, status);
        _jobs.GetByIdAsync(job.Id, default).Returns(job);

        var result = await Handler().HandleAsync(job.Id);

        result.ShouldBeFalse();
        await _jobs.DidNotReceive().UpdateAsync(Arg.Any<DownloadJob>(), default);
    }

    [Fact]
    public async Task JobNotFound_ReturnsFalse()
    {
        _jobs.GetByIdAsync(Arg.Any<Guid>(), default).Returns((DownloadJob?)null);

        var result = await Handler().HandleAsync(Guid.NewGuid());

        result.ShouldBeFalse();
    }

    private static void ApplyStatus(DownloadJob job, DownloadStatus status)
    {
        switch (status)
        {
            case DownloadStatus.Resolving:    job.MarkResolving(); break;
            case DownloadStatus.Downloading:  job.MarkDownloading(); break;
            case DownloadStatus.Finalising:   job.MarkFinalising(); break;
            case DownloadStatus.Completed:    job.MarkCompleted("/out/file.mp4", 1024); break;
            case DownloadStatus.Cancelled:    job.MarkCancelled(); break;
            case DownloadStatus.UrlUnavailable:  job.MarkUrlUnavailable("blocked"); break;
            case DownloadStatus.DownloadFailed:  job.MarkDownloadFailed("network error"); break;
            case DownloadStatus.FinaliseFailed:  job.MarkFinaliseFailed("disk full"); break;
        }
    }
}

// ──────────────────────────────────────────────────────────────
// RetryDownloadHandler
// ──────────────────────────────────────────────────────────────

public class RetryDownloadHandlerTests
{
    private readonly IDownloadJobRepository _jobs  = Substitute.For<IDownloadJobRepository>();
    private readonly IDownloadQueue         _queue = Substitute.For<IDownloadQueue>();
    private RetryDownloadHandler Handler() => new(_jobs, _queue);

    [Theory]
    [InlineData(DownloadStatus.UrlUnavailable)]
    [InlineData(DownloadStatus.DownloadFailed)]
    [InlineData(DownloadStatus.FinaliseFailed)]
    [InlineData(DownloadStatus.Cancelled)]
    public async Task FailedOrCancelledJob_CreatesNewJobAndEnqueues(DownloadStatus status)
    {
        var original = Fixtures.MakeJob();
        ApplyTerminal(original, status);
        _jobs.GetByIdAsync(original.Id, default).Returns(original);

        var result = await Handler().HandleAsync(original.Id);

        result.ShouldNotBeNull();
        result!.Status.ShouldBe(nameof(DownloadStatus.Queued));
        // New job created — not the original id
        result.JobId.ShouldNotBe(original.Id);

        await _jobs.Received(1).AddAsync(Arg.Any<DownloadJob>(), default);
        await _queue.Received(1).RequeueAsync(Arg.Any<Guid>(), original.StreamUrl, default);
    }

    [Fact]
    public async Task CompletedJob_ReturnsNull_NothingEnqueued()
    {
        var job = Fixtures.MakeJob();
        job.MarkCompleted("/out/file.mp4", 1024);
        _jobs.GetByIdAsync(job.Id, default).Returns(job);

        var result = await Handler().HandleAsync(job.Id);

        result.ShouldBeNull();
        await _queue.DidNotReceive().RequeueAsync(Arg.Any<Guid>(), Arg.Any<string>(), default);
    }

    [Fact]
    public async Task ActiveJob_ReturnsNull_NothingEnqueued()
    {
        var job = Fixtures.MakeJob(); // Queued — IsTerminal = false
        _jobs.GetByIdAsync(job.Id, default).Returns(job);

        var result = await Handler().HandleAsync(job.Id);

        result.ShouldBeNull();
        await _queue.DidNotReceive().RequeueAsync(Arg.Any<Guid>(), Arg.Any<string>(), default);
    }

    [Fact]
    public async Task JobNotFound_ReturnsNull()
    {
        _jobs.GetByIdAsync(Arg.Any<Guid>(), default).Returns((DownloadJob?)null);

        var result = await Handler().HandleAsync(Guid.NewGuid());

        result.ShouldBeNull();
    }

    private static void ApplyTerminal(DownloadJob job, DownloadStatus status)
    {
        switch (status)
        {
            case DownloadStatus.UrlUnavailable:  job.MarkUrlUnavailable("blocked"); break;
            case DownloadStatus.DownloadFailed:  job.MarkDownloadFailed("network"); break;
            case DownloadStatus.FinaliseFailed:  job.MarkFinaliseFailed("disk full"); break;
            case DownloadStatus.Cancelled:       job.MarkCancelled(); break;
        }
    }
}
