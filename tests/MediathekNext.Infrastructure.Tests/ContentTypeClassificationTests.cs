using MediathekNext.Domain.Enums;
using MediathekNext.Infrastructure.Catalog.MediathekView;
using Shouldly;
using Xunit;

namespace MediathekNext.Infrastructure.Tests;

public class ContentTypeClassificationTests
{
    private static FilmlisteEntry EntryWithTopic(string topic) =>
        new("ARD", topic, "Test Title", "09.03.2026", "20:00:00", "01:30:00",
            "100", "", "https://example.com/test.mp4", "", "", "", "", "", "", "", "", "", "false");

    [Theory]
    [InlineData("Film im Ersten")]
    [InlineData("ARD Spielfilm")]
    [InlineData("Kino")]
    [InlineData("Thriller der Woche")]
    [InlineData("Komödie")]
    public void ToEpisode_MovieTopic_ClassifiedAsMovie(string topic)
    {
        var (episode, _, _) = FilmlisteMapper.ToEpisode(EntryWithTopic(topic));
        episode!.ContentType.ShouldBe(ContentType.Movie);
    }

    [Theory]
    [InlineData("ARD Dokumentation")]
    [InlineData("Doku")]
    [InlineData("Reportage & Dokumentation")]
    public void ToEpisode_DocumentaryTopic_ClassifiedAsDocumentary(string topic)
    {
        var (episode, _, _) = FilmlisteMapper.ToEpisode(EntryWithTopic(topic));
        episode!.ContentType.ShouldBe(ContentType.Documentary);
    }

    [Theory]
    [InlineData("Tagesschau")]
    [InlineData("Tatort")]
    [InlineData("heute-show")]
    [InlineData("ZDF Morgenmagazin")]
    public void ToEpisode_SeriesOrNewsTopic_ClassifiedAsEpisode(string topic)
    {
        var (episode, _, _) = FilmlisteMapper.ToEpisode(EntryWithTopic(topic));
        episode!.ContentType.ShouldBe(ContentType.Episode);
    }
}
