using MediathekNext.Crawlers.Ard;
using MediathekNext.Crawlers.Core;
using Shouldly;
using Xunit;

namespace MediathekNext.Crawlers.Ard.Tests;

public class ParseArdEpisodeTests
{
    private static readonly ParseArdEpisodeHandler Handler = new();

    private static ArdEpisodeRaw MakeEpisode(
        IReadOnlyList<ArdStreamRaw>? streams    = null,
        IReadOnlyList<ArdStreamRaw>? streamsAd  = null,
        IReadOnlyList<ArdStreamRaw>? streamsDgs = null,
        IReadOnlyList<ArdStreamRaw>? streamsOv  = null,
        bool geoBlocked = false) => new(
        ItemId:         "test-id-123",
        BroadcasterKey: "ARD",
        ShowTitle:      "Tagesschau",
        EpisodeTitle:   "Tagesschau 20 Uhr",
        Description:    "Nachrichten",
        BroadcastTime:  new DateTimeOffset(2026, 4, 25, 20, 0, 0, TimeSpan.FromHours(2)),
        Duration:       TimeSpan.FromMinutes(15),
        GeoBlocked:     geoBlocked,
        Streams:        streams    ?? [],
        StreamsAd:      streamsAd  ?? [],
        StreamsDgs:     streamsDgs ?? [],
        StreamsOv:      streamsOv  ?? [],
        SubtitleUrls:   [],
        RelatedItemIds: []
    );

    private static ArdStreamRaw MakeStream(StreamQuality q = StreamQuality.Normal, string url = "https://example.com/video.mp4")
        => new(q, url);

    [Fact]
    public void Handle_Returns_Empty_When_No_Streams()
    {
        var result = Handler.Handle(new ParseArdEpisodeCommand(MakeEpisode()));
        result.ShouldBeEmpty();
    }

    [Fact]
    public void Handle_Returns_Single_German_Result_When_Only_Standard_Streams()
    {
        var ep     = MakeEpisode(streams: [MakeStream()]);
        var result = Handler.Handle(new ParseArdEpisodeCommand(ep));

        result.Count.ShouldBe(1);
        result[0].EpisodeTitle.ShouldBe("Tagesschau 20 Uhr");
        result[0].Streams.Count.ShouldBe(1);
        result[0].Streams[0].Language.ShouldBe(StreamLanguage.German);
        result[0].BroadcasterKey.ShouldBe("ARD");
    }

    [Fact]
    public void Handle_Returns_Four_Results_When_All_Variants_Present()
    {
        var ep = MakeEpisode(
            streams:    [MakeStream()],
            streamsAd:  [MakeStream(url: "https://example.com/ad.mp4")],
            streamsDgs: [MakeStream(url: "https://example.com/dgs.mp4")],
            streamsOv:  [MakeStream(url: "https://example.com/ov.mp4")]);

        var result = Handler.Handle(new ParseArdEpisodeCommand(ep));

        result.Count.ShouldBe(4);
        result.Select(r => r.Streams[0].Language).ShouldBe(
            [StreamLanguage.German, StreamLanguage.GermanAd, StreamLanguage.GermanDgs, StreamLanguage.Original],
            ignoreOrder: false);
    }

    [Fact]
    public void Handle_Applies_TitleSuffix_For_AudioDescription()
    {
        var ep     = MakeEpisode(streamsAd: [MakeStream()]);
        var result = Handler.Handle(new ParseArdEpisodeCommand(ep));

        result[0].EpisodeTitle.ShouldBe("Tagesschau 20 Uhr (Audiodeskription)");
    }

    [Fact]
    public void Handle_Applies_TitleSuffix_For_SignLanguage()
    {
        var ep     = MakeEpisode(streamsDgs: [MakeStream()]);
        var result = Handler.Handle(new ParseArdEpisodeCommand(ep));

        result[0].EpisodeTitle.ShouldBe("Tagesschau 20 Uhr (Gebärdensprache)");
    }

    [Fact]
    public void Handle_Applies_TitleSuffix_For_OriginalVersion()
    {
        var ep     = MakeEpisode(streamsOv: [MakeStream()]);
        var result = Handler.Handle(new ParseArdEpisodeCommand(ep));

        result[0].EpisodeTitle.ShouldBe("Tagesschau 20 Uhr (Originalversion)");
    }

    [Fact]
    public void Handle_Sets_GeoRestriction_When_GeoBlocked()
    {
        var ep     = MakeEpisode(streams: [MakeStream()], geoBlocked: true);
        var result = Handler.Handle(new ParseArdEpisodeCommand(ep));

        result[0].Geo.ShouldBe(GeoRestriction.De);
    }

    [Fact]
    public void Handle_Sets_NoGeoRestriction_When_Not_GeoBlocked()
    {
        var ep     = MakeEpisode(streams: [MakeStream()], geoBlocked: false);
        var result = Handler.Handle(new ParseArdEpisodeCommand(ep));

        result[0].Geo.ShouldBe(GeoRestriction.None);
    }

    [Fact]
    public void Handle_Preserves_Quality_In_Stream()
    {
        var ep = MakeEpisode(streams:
        [
            new ArdStreamRaw(StreamQuality.Sd,  "https://example.com/sd.mp4"),
            new ArdStreamRaw(StreamQuality.Hd,  "https://example.com/hd.mp4"),
        ]);
        var result = Handler.Handle(new ParseArdEpisodeCommand(ep));

        result[0].Streams.Select(s => s.Quality).ShouldBe(
            [StreamQuality.Sd, StreamQuality.Hd], ignoreOrder: false);
    }

    [Fact]
    public void Handle_Maps_SubtitleUrls_To_German_Subtitles()
    {
        var ep = MakeEpisode(streams: [MakeStream()]) with
        {
            SubtitleUrls = ["https://example.com/sub.xml"]
        };
        var result = Handler.Handle(new ParseArdEpisodeCommand(ep));

        result[0].Subtitles.Count.ShouldBe(1);
        result[0].Subtitles[0].Language.ShouldBe(StreamLanguage.German);
        result[0].Subtitles[0].Url.ShouldBe("https://example.com/sub.xml");
    }

    [Fact]
    public void Handle_Sets_WebsiteUrl_From_ItemId()
    {
        var ep     = MakeEpisode(streams: [MakeStream()]);
        var result = Handler.Handle(new ParseArdEpisodeCommand(ep));

        result[0].WebsiteUrl.ShouldBe("https://www.ardmediathek.de/video/test-id-123");
    }

    [Fact]
    public void Handle_Sets_EpisodeExternalId_For_German_Variant()
    {
        var ep     = MakeEpisode(streams: [MakeStream()]);
        var result = Handler.Handle(new ParseArdEpisodeCommand(ep));

        result[0].EpisodeExternalId.ShouldBe("test-id-123");
    }

    [Fact]
    public void Handle_Marks_HLS_Streams_Correctly()
    {
        var ep = MakeEpisode(streams:
        [
            new ArdStreamRaw(StreamQuality.Normal, "https://example.com/video.m3u8", IsHls: true)
        ]);
        var result = Handler.Handle(new ParseArdEpisodeCommand(ep));

        result[0].Streams[0].IsHls.ShouldBeTrue();
    }
}
