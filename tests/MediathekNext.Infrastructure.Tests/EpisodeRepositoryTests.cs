using MediathekNext.Domain.Entities;
using MediathekNext.Domain.Enums;
using MediathekNext.Infrastructure.Catalog;
using MediathekNext.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace MediathekNext.Infrastructure.Tests;

public class EpisodeRepositoryTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly EpisodeRepository _sut;

    public EpisodeRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;

        _db = new AppDbContext(options);
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();

        _sut = new EpisodeRepository(_db);

        SeedTestData();
    }

    private void SeedTestData()
    {
        var channel = new Channel { Id = "ard", Name = "ARD", ProviderKey = "mediathekview" };
        var show = new Show { Id = "show-1", Title = "Tagesschau", ChannelId = "ard", Channel = channel };
        var episodes = new[]
        {
            new Episode
            {
                Id = "ep-1",
                Title = "Tagesschau 20 Uhr",
                Description = "Die Nachrichten des Tages",
                ShowId = "show-1",
                Show = show,
                BroadcastDate = DateTimeOffset.UtcNow.AddDays(-1),
                Duration = TimeSpan.FromMinutes(15),
                Streams =
                [
                    new EpisodeStream
                    {
                        Id = "stream-1",
                        EpisodeId = "ep-1",
                        Quality = VideoQuality.High,
                        Url = "https://example.com/ep1-hd.mp4",
                        Format = "mp4"
                    }
                ]
            },
            new Episode
            {
                Id = "ep-2",
                Title = "Tagesthemen",
                ShowId = "show-1",
                Show = show,
                BroadcastDate = DateTimeOffset.UtcNow.AddDays(-2),
                Duration = TimeSpan.FromMinutes(30),
                Streams = []
            }
        };

        _db.Channels.Add(channel);
        _db.Shows.Add(show);
        _db.Episodes.AddRange(episodes);
        _db.SaveChanges();
    }

    [Fact]
    public async Task GetByIdAsync_ExistingEpisode_ReturnsEpisodeWithStreams()
    {
        var result = await _sut.GetByIdAsync("ep-1");

        result.ShouldNotBeNull();
        result.Title.ShouldBe("Tagesschau 20 Uhr");
        result.Streams.ShouldHaveSingleItem();
        result.Streams.First().Quality.ShouldBe(VideoQuality.High);
    }

    [Fact]
    public async Task GetByIdAsync_NonExistentId_ReturnsNull()
    {
        var result = await _sut.GetByIdAsync("does-not-exist");
        result.ShouldBeNull();
    }

    [Fact]
    public async Task SearchAsync_MatchingTitle_ReturnsResults()
    {
        var results = await _sut.SearchAsync("tagesschau");

        results.ShouldNotBeEmpty();
        results.ShouldContain(e => e.Title == "Tagesschau 20 Uhr");
    }

    [Fact]
    public async Task SearchAsync_MatchingDescription_ReturnsResults()
    {
        var results = await _sut.SearchAsync("nachrichten");

        results.ShouldNotBeEmpty();
        results.ShouldContain(e => e.Id == "ep-1");
    }

    [Fact]
    public async Task SearchAsync_NoMatch_ReturnsEmpty()
    {
        var results = await _sut.SearchAsync("zdfmediathek-xyz-nomatch");
        results.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetByChannelAsync_KnownChannel_ReturnsEpisodes()
    {
        var results = await _sut.GetByChannelAsync("ard");

        results.Count.ShouldBe(2);
        results.ShouldAllBe(e => e.Show.Channel.Id == "ard");
    }

    [Fact]
    public async Task UpsertManyAsync_NewEpisodes_AreInserted()
    {
        var channel = await _db.Channels.FindAsync("ard");
        var show = await _db.Shows.FindAsync("show-1");

        var newEpisodes = new[]
        {
            new Episode
            {
                Id = "ep-new-1",
                Title = "New Episode",
                ShowId = "show-1",
                Show = show!,
                BroadcastDate = DateTimeOffset.UtcNow,
                Duration = TimeSpan.FromMinutes(45),
                Streams = []
            }
        };

        await _sut.UpsertManyAsync(newEpisodes);

        var result = await _sut.GetByIdAsync("ep-new-1");
        result.ShouldNotBeNull();
        result.Title.ShouldBe("New Episode");
    }

    public void Dispose()
    {
        _db.Database.CloseConnection();
        _db.Dispose();
    }
}
