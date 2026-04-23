using MediathekNext.Domain.Enums;
using MediathekNext.Infrastructure.Catalog.MediathekView;
using Shouldly;
using Xunit;

namespace MediathekNext.Infrastructure.Tests;

public class FilmlisteMapperTests
{
    private static FilmlisteEntry MakeEntry(
        string channel = "ARD",
        string topic = "Tagesschau",
        string title = "Tagesschau 20 Uhr",
        string date = "09.03.2026",
        string time = "20:00:00",
        string duration = "00:15:00",
        string urlSd = "https://example.com/episode.mp4",
        string urlHd = "",
        string urlSmall = "") =>
        new(channel, topic, title, date, time, duration,
            "100", "Die Nachrichten des Tages",
            urlSd, "https://ard.de", "", "", urlHd, "", urlSmall, "", "", "", "false");

    [Fact]
    public void ToEpisode_ValidEntry_ReturnsMappedEpisode()
    {
        var (episode, channel, show) = FilmlisteMapper.ToEpisode(MakeEntry());

        episode.ShouldNotBeNull();
        episode.Title.ShouldBe("Tagesschau 20 Uhr");
        episode.Duration.ShouldBe(TimeSpan.FromMinutes(15));
        channel.Id.ShouldBe("ard");
        show.Title.ShouldBe("Tagesschau");
    }

    [Fact]
    public void ToEpisode_MissingDate_ReturnsNullEpisode()
    {
        var entry = MakeEntry(date: "");
        var (episode, _, _) = FilmlisteMapper.ToEpisode(entry);
        episode.ShouldBeNull();
    }

    [Fact]
    public void ToEpisode_SdUrlPresent_CreatesStandardQualityStream()
    {
        var (episode, _, _) = FilmlisteMapper.ToEpisode(
            MakeEntry(urlSd: "https://example.com/sd.mp4"));

        episode!.Streams.ShouldContain(s => s.Quality == VideoQuality.Standard);
        episode.Streams.First(s => s.Quality == VideoQuality.Standard)
            .Url.ShouldBe("https://example.com/sd.mp4");
    }

    [Fact]
    public void ToEpisode_FullHdUrl_CreatesHighQualityStream()
    {
        var (episode, _, _) = FilmlisteMapper.ToEpisode(
            MakeEntry(
                urlSd: "https://example.com/sd.mp4",
                urlHd: "https://example.com/hd.mp4"));

        episode!.Streams.ShouldContain(s => s.Quality == VideoQuality.High);
        episode.Streams.First(s => s.Quality == VideoQuality.High)
            .Url.ShouldBe("https://example.com/hd.mp4");
    }

    [Fact]
    public void ToEpisode_SuffixHdUrl_ResolvesCorrectly()
    {
        // Filmliste suffix format: "NNN|suffix"
        // "3|hd.mp4" means: strip 6 chars from base URL, append "hd.mp4"
        var baseUrl = "https://example.com/sd.mp4";
        var hdSuffix = "6|hd.mp4"; // strip "sd.mp4" (6 chars), append "hd.mp4"

        var (episode, _, _) = FilmlisteMapper.ToEpisode(
            MakeEntry(urlSd: baseUrl, urlHd: hdSuffix));

        episode!.Streams.ShouldContain(s => s.Quality == VideoQuality.High);
        episode.Streams.First(s => s.Quality == VideoQuality.High)
            .Url.ShouldBe("https://example.com/hd.mp4");
    }

    [Fact]
    public void ToEpisode_SameChannelTopicTitle_ProducesSameEpisodeId()
    {
        var entry1 = MakeEntry();
        var entry2 = MakeEntry();

        var (ep1, _, _) = FilmlisteMapper.ToEpisode(entry1);
        var (ep2, _, _) = FilmlisteMapper.ToEpisode(entry2);

        ep1!.Id.ShouldBe(ep2!.Id);
    }

    [Fact]
    public void ToEpisode_DifferentUrls_ProduceDifferentEpisodeIds()
    {
        var entry1 = MakeEntry(urlSd: "https://example.com/ep1.mp4");
        var entry2 = MakeEntry(urlSd: "https://example.com/ep2.mp4");

        var (ep1, _, _) = FilmlisteMapper.ToEpisode(entry1);
        var (ep2, _, _) = FilmlisteMapper.ToEpisode(entry2);

        ep1!.Id.ShouldNotBe(ep2!.Id);
    }

    [Fact]
    public void ToEpisode_M3u8Url_InfersCorrectFormat()
    {
        var (episode, _, _) = FilmlisteMapper.ToEpisode(
            MakeEntry(urlSd: "https://example.com/stream.m3u8"));

        episode!.Streams.First().Format.ShouldBe("m3u8");
    }

    [Fact]
    public void ToEpisode_BroadcastDate_ParsedWithCetOffset()
    {
        var (episode, _, _) = FilmlisteMapper.ToEpisode(
            MakeEntry(date: "09.03.2026", time: "20:00:00"));

        episode!.BroadcastDate.Offset.ShouldBe(TimeSpan.FromHours(1));
        episode.BroadcastDate.Hour.ShouldBe(20);
    }
}
