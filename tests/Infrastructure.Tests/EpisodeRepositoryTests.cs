using System.Linq;
using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Enums;
using Krautwatch.Infrastructure.Catalog;
using Krautwatch.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace Krautwatch.Infrastructure.Tests;

[Collection(PostgresCollection.Name)]
public class EpisodeRepositoryTests(PostgresFixture postgres) : IAsyncLifetime
{
    private AppDbContext _db = null!;
    private EpisodeRepository _sut = null!;

    public async ValueTask InitializeAsync()
    {
        _db = new AppDbContext(await postgres.CreateDatabaseAsync());
        _sut = new EpisodeRepository(_db);

        SeedTestData();
    }

    public ValueTask DisposeAsync()
    {
        _db.Dispose();
        return ValueTask.CompletedTask;
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

    [Fact]
    public async Task UpsertManyAsync_FreshCrawlGraph_InsertsChannelShowEpisodeAndStream()
    {
        // A crawl produces a brand-new Channel/Show/Episode/Stream graph, with the same Channel and
        // Show instance shared across episodes — the shape the broadcaster adapters emit.
        var channel = new Channel { Id = "zdf", Name = "ZDF", ProviderKey = "zdf" };
        var show = new Show { Id = "zdf:heute-show", Title = "heute-show", ChannelId = "zdf", Channel = channel };

        var episodes = Enumerable.Range(1, 2).Select(i => new Episode
        {
            Id = $"zdf:doc-{i}",
            Title = $"heute-show {i}",
            ShowId = show.Id,
            Show = show, // shared instance across the batch
            BroadcastDate = DateTimeOffset.UtcNow.AddDays(-i),
            Duration = TimeSpan.FromMinutes(30),
            Streams =
            [
                new EpisodeStream
                {
                    Id = $"zdf:doc-{i}:v",
                    EpisodeId = $"zdf:doc-{i}",
                    Quality = VideoQuality.High,
                    Url = $"https://cdn.zdf.de/doc-{i}.mp4",
                    Format = "mp4"
                }
            ]
        }).ToList();

        await _sut.UpsertManyAsync(episodes);

        (await _db.Channels.FindAsync("zdf")).ShouldNotBeNull();
        (await _db.Shows.FindAsync("zdf:heute-show")).ShouldNotBeNull();

        var persisted = await _sut.GetByIdAsync("zdf:doc-1");
        persisted.ShouldNotBeNull();
        persisted.Show.Channel.Name.ShouldBe("ZDF");
        persisted.Streams.ShouldHaveSingleItem().Url.ShouldBe("https://cdn.zdf.de/doc-1.mp4");
    }

    [Fact]
    public async Task UpsertManyAsync_RecrawlSameEpisode_UpdatesInPlaceWithoutDuplicating()
    {
        var channel = new Channel { Id = "ardx", Name = "ARD", ProviderKey = "ardx" };
        var show = new Show { Id = "ardx:extra-3", Title = "extra 3", ChannelId = "ardx", Channel = channel };
        Episode Build(string title) => new()
        {
            Id = "ardx:ep-x",
            Title = title,
            ShowId = show.Id,
            Show = show,
            BroadcastDate = DateTimeOffset.UtcNow,
            Duration = TimeSpan.FromMinutes(44),
            Streams =
            [
                new EpisodeStream { Id = "ardx:ep-x:v", EpisodeId = "ardx:ep-x", Quality = VideoQuality.High, Url = "https://cdn/x.mp4", Format = "mp4" }
            ]
        };

        await _sut.UpsertManyAsync([Build("first title")]);
        await _sut.UpsertManyAsync([Build("updated title")]);

        var all = await _sut.GetByShowAsync("ardx:extra-3");
        all.ShouldHaveSingleItem().Title.ShouldBe("updated title");
    }

}
