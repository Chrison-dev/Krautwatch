using Krautwatch.Application.Downloads;
using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Enums;
using Krautwatch.Domain.Interfaces;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Krautwatch.Application.Tests;

public class NzbTokenTests
{
    // Mirrors the NZB the Newznab indexer serves (newzbin namespace, un-namespaced type attribute).
    private const string Nzb =
        """
        <?xml version="1.0" encoding="UTF-8"?>
        <nzb xmlns="http://www.newzbin.com/DTD/2003/nzb">
          <head>
            <meta type="krautwatch-token">zdf:content/documents/heute-show-123.json</meta>
          </head>
        </nzb>
        """;

    [Fact]
    public void Reads_the_token_back_out_of_the_synthetic_nzb()
    {
        NzbToken.Read(Nzb).ShouldBe("zdf:content/documents/heute-show-123.json");
    }

    [Fact]
    public void Returns_null_for_an_nzb_without_the_token_meta()
    {
        NzbToken.Read("""<nzb><head><meta type="password">x</meta></head></nzb>""").ShouldBeNull();
    }

    [Fact]
    public void Returns_null_for_malformed_xml()
    {
        NzbToken.Read("not xml at all").ShouldBeNull();
    }
}

public class AddDownloadByTokenHandlerTests
{
    private static Episode EpisodeWithStream(string id, bool geoRestricted = false) => new()
    {
        Id = id,
        Title = "heute-show",
        ShowId = "zdf:heute-show",
        BroadcastDate = DateTimeOffset.UtcNow,
        Duration = TimeSpan.FromMinutes(30),
        GeoRestricted = geoRestricted,
        Streams =
        [
            new EpisodeStream { Id = $"{id}:v", EpisodeId = id, Quality = VideoQuality.High, Url = "https://cdn/x.mp4", Format = "mp4" }
        ],
    };

    [Fact]
    public async Task Creates_and_enqueues_a_job_for_a_known_token()
    {
        var episodes = Substitute.For<IEpisodeRepository>();
        episodes.GetByIdAsync("zdf:1", Arg.Any<CancellationToken>()).Returns(EpisodeWithStream("zdf:1"));
        var jobs = Substitute.For<IDownloadJobRepository>();
        var queue = Substitute.For<IDownloadQueue>();

        var jobId = await new AddDownloadByTokenHandler(episodes, jobs, queue).HandleAsync("zdf:1", TestContext.Current.CancellationToken);

        jobId.ShouldNotBeNull();
        await jobs.Received(1).AddAsync(Arg.Is<DownloadJob>(j => j != null && j.EpisodeId == "zdf:1" && j.StreamUrl == "https://cdn/x.mp4"), Arg.Any<CancellationToken>());
        await queue.Received(1).EnqueueAsync(jobId!.Value, "https://cdn/x.mp4", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Snapshots_the_episodes_geo_restriction_onto_the_job()
    {
        var episodes = Substitute.For<IEpisodeRepository>();
        episodes.GetByIdAsync("kika:1", Arg.Any<CancellationToken>())
            .Returns(EpisodeWithStream("kika:1", geoRestricted: true));
        var jobs = Substitute.For<IDownloadJobRepository>();
        var queue = Substitute.For<IDownloadQueue>();

        await new AddDownloadByTokenHandler(episodes, jobs, queue).HandleAsync("kika:1", TestContext.Current.CancellationToken);

        await jobs.Received(1).AddAsync(Arg.Is<DownloadJob>(j => j != null && j.GeoRestricted), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unknown_token_returns_null_and_enqueues_nothing()
    {
        var episodes = Substitute.For<IEpisodeRepository>();
        episodes.GetByIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((Episode?)null);
        var jobs = Substitute.For<IDownloadJobRepository>();
        var queue = Substitute.For<IDownloadQueue>();

        var jobId = await new AddDownloadByTokenHandler(episodes, jobs, queue).HandleAsync("nope", TestContext.Current.CancellationToken);

        jobId.ShouldBeNull();
        await queue.DidNotReceive().EnqueueAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Episode_without_a_stream_returns_null()
    {
        var episodes = Substitute.For<IEpisodeRepository>();
        episodes.GetByIdAsync("zdf:2", Arg.Any<CancellationToken>()).Returns(new Episode
        {
            Id = "zdf:2", Title = "x", ShowId = "s", BroadcastDate = DateTimeOffset.UtcNow, Duration = TimeSpan.Zero,
        });
        var jobs = Substitute.For<IDownloadJobRepository>();
        var queue = Substitute.For<IDownloadQueue>();

        var jobId = await new AddDownloadByTokenHandler(episodes, jobs, queue).HandleAsync("zdf:2", TestContext.Current.CancellationToken);

        jobId.ShouldBeNull();
        await jobs.DidNotReceive().AddAsync(Arg.Any<DownloadJob>(), Arg.Any<CancellationToken>());
    }
}
