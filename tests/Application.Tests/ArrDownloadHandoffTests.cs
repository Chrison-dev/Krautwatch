using Krautwatch.Application.Downloads;
using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Enums;
using Krautwatch.Domain.Interfaces;
using Krautwatch.Domain.ValueObjects;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Krautwatch.Application.Tests;

/// <summary>
/// Covers the hand-off from an `*arr` grab to a download job. Every case here comes from driving a real
/// Sonarr 4.0.19 against the running fleet — each was a blocker that only appeared end to end.
/// </summary>
public class ArrDownloadHandoffTests
{
    private readonly IEpisodeRepository _episodes = Substitute.For<IEpisodeRepository>();
    private readonly IDownloadJobRepository _jobs = Substitute.For<IDownloadJobRepository>();
    private readonly IDownloadQueue _queue = Substitute.For<IDownloadQueue>();

    private const string Release = "heute-show.S2026E15.GERMAN.1080p.WEB.h264";

    private AddDownloadByTokenHandler Handler()
    {
        _episodes.GetByIdAsync("zdf:1", Arg.Any<CancellationToken>()).Returns(new Episode
        {
            Id = "zdf:1",
            Title = "heute-show vom 15. Mai 2026",
            ShowId = "zdf:heute-show",
            BroadcastDate = DateTimeOffset.UtcNow,
            Duration = TimeSpan.FromMinutes(40),
            Streams = [new EpisodeStream { Url = "https://cdn.invalid/a.mp4", Quality = VideoQuality.High }],
        });
        return new AddDownloadByTokenHandler(_episodes, _jobs, _queue);
    }

    [Fact]
    public async Task The_release_name_is_carried_onto_the_job()
    {
        await Handler().HandleAsync(
            new ReleaseToken("zdf:1", 234791).Encode(), Release, ct: TestContext.Current.CancellationToken);

        await _jobs.Received(1).AddAsync(
            Arg.Is<DownloadJob>(j => j != null && j.ReleaseName == Release), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_download_started_from_our_own_ui_has_no_release_name()
    {
        // Null is meaningful: it selects the human-readable library layout rather than the release layout.
        await Handler().HandleAsync("zdf:1", ct: TestContext.Current.CancellationToken);

        await _jobs.Received(1).AddAsync(
            Arg.Is<DownloadJob>(j => j != null && j.ReleaseName == null), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_release_name_is_stored_as_null(string releaseName)
    {
        await Handler().HandleAsync("zdf:1", releaseName, ct: TestContext.Current.CancellationToken);

        await _jobs.Received(1).AddAsync(
            Arg.Is<DownloadJob>(j => j != null && j.ReleaseName == null), Arg.Any<CancellationToken>());
    }
}

/// <summary>
/// The synthetic NZB has to satisfy Sonarr's own validation before it will hand it to a download client.
/// </summary>
public class SyntheticNzbTests
{
    private const string Token = "zdf:content/documents/heute-show-vom-15-mai-2026-100.json|tvdb=234791";
    private const string Release = "heute-show.S2026E15.GERMAN.1080p.WEB.h264";

    /// <summary>Mirrors the NZB the indexer serves; kept here so the contract is asserted, not assumed.</summary>
    private static string Nzb(string token, string release) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <nzb xmlns="http://www.newzbin.com/DTD/2003/nzb">
          <head>
            <meta type="{NzbToken.MetaType}">{token}</meta>
          </head>
          <file poster="krautwatch@localhost" date="1785555131" subject="&quot;{release}.mp4&quot; yEnc (1/1)">
            <groups><group>alt.binaries.krautwatch</group></groups>
            <segments><segment bytes="1" number="1">krautwatch@localhost</segment></segments>
          </file>
        </nzb>
        """;

    [Fact]
    public void The_token_survives_the_round_trip()
    {
        NzbToken.Read(Nzb(Token, Release)).ShouldBe(Token);
    }

    [Fact]
    public void The_release_name_is_recoverable_from_the_file_subject()
    {
        // Sonarr uploads the NZB as multipart and repeats the release title nowhere in the query string,
        // so the subject is the only place we get it back from.
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Nzb(Token, Release)));
        NzbToken.ReadReleaseName(stream).ShouldBe(Release);
    }

    [Fact]
    public void The_container_extension_is_stripped_from_the_release_name()
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Nzb(Token, Release)));
        NzbToken.ReadReleaseName(stream).ShouldNotEndWith(".mp4");
    }

    [Fact]
    public void An_nzb_with_no_file_element_yields_no_release_name()
    {
        // The shape we used to emit. Sonarr rejects it outright with "Invalid NZB: No files", so this is
        // also a reminder of why the placeholder file exists at all.
        const string tokenOnly = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <nzb xmlns="http://www.newzbin.com/DTD/2003/nzb">
              <head><meta type="{NzbToken.MetaType}">zdf:1</meta></head>
            </nzb>
            """;

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(tokenOnly));
        NzbToken.ReadReleaseName(stream).ShouldBeNull();
        NzbToken.Read(tokenOnly).ShouldBe("zdf:1");   // the token still reads, so old NZBs keep working
    }

    [Fact]
    public void Malformed_xml_is_reported_as_nothing_rather_than_throwing()
    {
        using var stream = new MemoryStream("not xml at all"u8.ToArray());
        NzbToken.ReadReleaseName(stream).ShouldBeNull();
    }
}
