using MediathekNext.Application.Catalog;
using MediathekNext.Domain.Entities;
using MediathekNext.Domain.Enums;
using MediathekNext.Domain.Interfaces;
using NSubstitute;
using Shouldly;
using Xunit;

namespace MediathekNext.Application.Tests;

public class SearchCatalogQueryHandlerTests
{
    private readonly IEpisodeRepository _repository = Substitute.For<IEpisodeRepository>();
    private readonly SearchCatalogQueryHandler _sut;

    public SearchCatalogQueryHandlerTests()
    {
        _sut = new SearchCatalogQueryHandler(_repository);
    }

    [Fact]
    public async Task HandleAsync_WithResults_ReturnsMappedResponses()
    {
        // Arrange
        var channel = new Channel { Id = "ard", Name = "ARD", ProviderKey = "mediathekview" };
        var show = new Show { Id = "show-1", Title = "Tagesschau", ChannelId = "ard", Channel = channel };
        var broadcastDate = DateTimeOffset.UtcNow.AddDays(-1);

        var episodes = new List<Episode>
        {
            new()
            {
                Id = "ep-1",
                Title = "Tagesschau 20 Uhr",
                Description = "Die Nachrichten",
                ShowId = "show-1",
                Show = show,
                BroadcastDate = broadcastDate,
                Duration = TimeSpan.FromMinutes(15),
                Streams =
                [
                    new EpisodeStream
                    {
                        Id = "s-1",
                        EpisodeId = "ep-1",
                        Quality = VideoQuality.High,
                        Url = "https://example.com/ep.mp4",
                        Format = "mp4"
                    }
                ]
            }
        };

        _repository
            .SearchAsync("tagesschau", Arg.Any<CancellationToken>())
            .Returns(episodes);

        // Act
        var results = await _sut.HandleAsync(new SearchCatalogQuery("tagesschau"));

        // Assert
        results.Count.ShouldBe(1);

        var result = results[0];
        result.EpisodeId.ShouldBe("ep-1");
        result.EpisodeTitle.ShouldBe("Tagesschau 20 Uhr");
        result.ShowTitle.ShouldBe("Tagesschau");
        result.ChannelId.ShouldBe("ard");
        result.ChannelName.ShouldBe("ARD");
        result.BroadcastDate.ShouldBe(broadcastDate);
        result.Duration.ShouldBe(TimeSpan.FromMinutes(15));
        result.Streams.ShouldHaveSingleItem();
        result.Streams[0].Quality.ShouldBe("High");
    }

    [Fact]
    public async Task HandleAsync_NoResults_ReturnsEmptyList()
    {
        _repository
            .SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<Episode>());

        var results = await _sut.HandleAsync(new SearchCatalogQuery("noresults"));

        results.ShouldBeEmpty();
    }

    [Fact]
    public async Task HandleAsync_CallsRepositoryWithCorrectQuery()
    {
        _repository
            .SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<Episode>());

        await _sut.HandleAsync(new SearchCatalogQuery("tagesthemen"));

        await _repository
            .Received(1)
            .SearchAsync("tagesthemen", Arg.Any<CancellationToken>());
    }
}
