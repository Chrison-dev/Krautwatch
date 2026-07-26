using Krautwatch.Application.Downloads;
using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Enums;
using Krautwatch.Domain.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

namespace Krautwatch.Application.Tests;

public class RunDownloadHandlerTests
{
    // A job the repository has already claimed (Downloading), as the worker hands it over.
    private static DownloadJob ClaimedJob()
    {
        var job = new DownloadJob
        {
            Id = Guid.NewGuid(),
            EpisodeId = "zdf:1",
            Episode = new Episode
            {
                Id = "zdf:1", Title = "heute-show", ShowId = "zdf:heute-show",
                BroadcastDate = DateTimeOffset.UtcNow, Duration = TimeSpan.FromMinutes(30),
                Show = new Show { Id = "zdf:heute-show", Title = "heute-show", ChannelId = "zdf",
                    Channel = new Channel { Id = "zdf", Name = "ZDF", ProviderKey = "zdf" } },
            },
            StreamUrl = "https://cdn/x.mp4",
            Quality = VideoQuality.High,
        };
        job.MarkClaiming("downloader-test");
        return job;
    }

    private static (IDownloadJobRepository jobs, IDownloadProvider provider, ISettingsRepository settings) Deps()
    {
        var jobs = Substitute.For<IDownloadJobRepository>();
        var settings = Substitute.For<ISettingsRepository>();
        settings.GetAsync(Arg.Any<CancellationToken>()).Returns(new AppSettings { DownloadDirectory = "/downloads" });
        return (jobs, Substitute.For<IDownloadProvider>(), settings);
    }

    private static RunDownloadHandler Sut(IDownloadJobRepository jobs, IDownloadProvider provider, ISettingsRepository settings) =>
        new(jobs, provider, settings, NullLogger<RunDownloadHandler>.Instance);

    [Fact]
    public async Task Downloads_and_marks_the_job_completed()
    {
        var job = ClaimedJob();
        var (jobs, provider, settings) = Deps();
        provider.DownloadAsync(job, "/downloads", Arg.Any<IProgress<double>>(), Arg.Any<CancellationToken>())
            .Returns(new DownloadResult("/downloads/ZDF/heute-show/x.mp4", 123_456));

        await Sut(jobs, provider, settings).HandleAsync(job);

        job.Status.ShouldBe(DownloadStatus.Completed);
        job.OutputPath.ShouldBe("/downloads/ZDF/heute-show/x.mp4");
        job.FileSizeBytes.ShouldBe(123_456);
        await jobs.Received().UpdateAsync(job, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Provider_failure_marks_the_job_download_failed()
    {
        var job = ClaimedJob();
        var (jobs, provider, settings) = Deps();
        provider.DownloadAsync(Arg.Any<DownloadJob>(), Arg.Any<string>(), Arg.Any<IProgress<double>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("stream 403"));

        await Sut(jobs, provider, settings).HandleAsync(job);

        job.Status.ShouldBe(DownloadStatus.DownloadFailed);
        job.ErrorMessage.ShouldBe("stream 403");
    }

    [Fact]
    public async Task A_job_missing_episode_metadata_fails_without_downloading()
    {
        var job = new DownloadJob { Id = Guid.NewGuid(), EpisodeId = "x", StreamUrl = "https://cdn/x.mp4", Quality = VideoQuality.High };
        var (jobs, provider, settings) = Deps();

        await Sut(jobs, provider, settings).HandleAsync(job);

        job.Status.ShouldBe(DownloadStatus.DownloadFailed);
        await provider.DidNotReceive().DownloadAsync(Arg.Any<DownloadJob>(), Arg.Any<string>(), Arg.Any<IProgress<double>>(), Arg.Any<CancellationToken>());
    }
}
