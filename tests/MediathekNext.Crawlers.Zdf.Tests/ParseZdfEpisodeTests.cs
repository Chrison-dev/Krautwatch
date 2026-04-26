using MediathekNext.Crawlers.Core;
using MediathekNext.Crawlers.Zdf;
using Shouldly;
using Xunit;

namespace MediathekNext.Crawlers.Zdf.Tests;

public class ParseZdfEpisodeTests
{
    private static readonly ParseZdfEpisodeHandler Handler = new();

    private static ZdfEpisodeRef MakeEpisodeRef(string topic = "Krimi", string title = "Ein starkes Team") =>
        new(
            Topic:            topic,
            BroadcasterKey:   "ZDF",
            Title:            title,
            Description:      "Beschreibung",
            WebsiteUrl:       "https://www.zdf.de/serien/ein-starkes-team",
            BroadcastTime:    new DateTimeOffset(2026, 4, 25, 20, 15, 0, TimeSpan.FromHours(2)),
            DownloadUrlsByType: new Dictionary<string, string> { ["default"] = "https://download.url" }
        );

    private static ZdfDownloadRaw MakeDownload(
        IReadOnlyList<ZdfStreamRaw>? streams   = null,
        IReadOnlyList<ZdfSubtitleRaw>? subs    = null,
        GeoRestriction geo                     = GeoRestriction.None) => new(
        Duration:  TimeSpan.FromMinutes(90),
        Geo:       geo,
        Streams:   streams ?? [new ZdfStreamRaw(StreamQuality.Normal, StreamLanguage.German, "https://example.com/video.mp4")],
        Subtitles: subs ?? []
    );

    [Fact]
    public void Handle_Returns_Null_When_No_Streams()
    {
        var download = MakeDownload(streams: []);
        var result   = Handler.Handle(new ParseZdfEpisodeCommand(MakeEpisodeRef(), download, "default"));

        result.ShouldBeNull();
    }

    [Fact]
    public void Handle_Returns_CrawlResult_With_Correct_Metadata()
    {
        var result = Handler.Handle(new ParseZdfEpisodeCommand(
            MakeEpisodeRef(), MakeDownload(), "default"));

        result.ShouldNotBeNull();
        result.BroadcasterKey.ShouldBe("ZDF");
        result.ShowTitle.ShouldBe("Krimi");
        result.EpisodeTitle.ShouldBe("Ein starkes Team");
        result.Description.ShouldBe("Beschreibung");
        result.Duration.ShouldBe(TimeSpan.FromMinutes(90));
    }

    [Fact]
    public void Handle_Uses_Title_As_ShowTitle_When_Topic_Empty()
    {
        var ep     = MakeEpisodeRef(topic: "");
        var result = Handler.Handle(new ParseZdfEpisodeCommand(ep, MakeDownload(), "default"));

        result.ShouldNotBeNull();
        result.ShowTitle.ShouldBe("Ein starkes Team");
    }

    [Fact]
    public void Handle_Preserves_Stream_Language_For_Default_VodType()
    {
        var download = MakeDownload(streams:
        [
            new ZdfStreamRaw(StreamQuality.Normal, StreamLanguage.German,   "https://example.com/de.mp4"),
            new ZdfStreamRaw(StreamQuality.Normal, StreamLanguage.GermanAd, "https://example.com/ad.mp4"),
        ]);

        var result = Handler.Handle(new ParseZdfEpisodeCommand(MakeEpisodeRef(), download, "default"));

        result.ShouldNotBeNull();
        result.Streams.Select(s => s.Language).ShouldBe(
            [StreamLanguage.German, StreamLanguage.GermanAd], ignoreOrder: false);
    }

    [Fact]
    public void Handle_Overrides_Language_To_GermanDgs_When_VodType_Contains_Dgs()
    {
        var download = MakeDownload(streams:
        [
            new ZdfStreamRaw(StreamQuality.Normal, StreamLanguage.German, "https://example.com/dgs.mp4"),
        ]);

        var result = Handler.Handle(new ParseZdfEpisodeCommand(MakeEpisodeRef(), download, "hbbtvDgs"));

        result.ShouldNotBeNull();
        result.Streams[0].Language.ShouldBe(StreamLanguage.GermanDgs);
    }

    [Fact]
    public void Handle_Sets_GeoRestriction_From_Download()
    {
        var download = MakeDownload(geo: GeoRestriction.De);
        var result   = Handler.Handle(new ParseZdfEpisodeCommand(MakeEpisodeRef(), download, "default"));

        result.ShouldNotBeNull();
        result.Geo.ShouldBe(GeoRestriction.De);
    }

    [Fact]
    public void Handle_Maps_Subtitles_Correctly()
    {
        var download = MakeDownload(subs:
        [
            new ZdfSubtitleRaw(StreamLanguage.German,   "https://example.com/de.xml"),
            new ZdfSubtitleRaw(StreamLanguage.GermanAd, "https://example.com/ad.xml"),
        ]);

        var result = Handler.Handle(new ParseZdfEpisodeCommand(MakeEpisodeRef(), download, "default"));

        result.ShouldNotBeNull();
        result.Subtitles.Count.ShouldBe(2);
        result.Subtitles[0].Language.ShouldBe(StreamLanguage.German);
        result.Subtitles[1].Language.ShouldBe(StreamLanguage.GermanAd);
    }

    [Fact]
    public void Handle_Sets_WebsiteUrl_From_EpisodeRef()
    {
        var result = Handler.Handle(new ParseZdfEpisodeCommand(
            MakeEpisodeRef(), MakeDownload(), "default"));

        result.ShouldNotBeNull();
        result.WebsiteUrl.ShouldBe("https://www.zdf.de/serien/ein-starkes-team");
    }
}
