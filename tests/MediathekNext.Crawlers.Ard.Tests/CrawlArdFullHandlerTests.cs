using MediathekNext.Crawlers.Ard;
using MediathekNext.Crawlers.Core;
using NSubstitute;
using Shouldly;
using Xunit;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediathekNext.Crawlers.Ard.Tests;

public class CrawlArdFullHandlerTests
{
    private readonly IArdClient             _client   = Substitute.For<IArdClient>();
    private readonly IRawResponseStore      _rawStore = Substitute.For<IRawResponseStore>();
    private readonly ICrawlRepository       _repo     = Substitute.For<ICrawlRepository>();
    private readonly ParseArdEpisodeHandler _parser   = new();
    private readonly CrawlArdFullHandler    _handler;

    public CrawlArdFullHandlerTests()
    {
        var rawStoreHandler = new StoreRawResponseHandler(_rawStore);
        var persistHandler  = new PersistCrawlResultHandler(_repo);
        _handler = new CrawlArdFullHandler(
            _client, _parser, rawStoreHandler, persistHandler,
            NullLogger<CrawlArdFullHandler>.Instance);
    }

    [Fact]
    public async Task HandleAsync_Returns_Summary_With_Correct_Source()
    {
        SetupEmptyClients();

        var summary = await _handler.HandleAsync(new CrawlArdFullCommand());

        summary.Source.ShouldBe("ard");
    }

    [Fact]
    public async Task HandleAsync_Stores_RawJson_For_Each_Episode_Fetched()
    {
        var episode = MakeEpisode("item-1");
        var topicUrl = "https://api.ardmediathek.de/topics/ard";

        _client.FetchTopicUrlsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _client.FetchTopicUrlsAsync("ard", Arg.Any<CancellationToken>())
            .Returns([topicUrl]);
        _client.FetchTopicItemIdsAsync(topicUrl, Arg.Any<CancellationToken>())
            .Returns(["item-1"]);
        _client.FetchEpisodeAsync("item-1", Arg.Any<CancellationToken>())
            .Returns(("raw-json-1", episode));

        await _handler.HandleAsync(new CrawlArdFullCommand());

        await _rawStore.Received(1).StoreAsync("ard", "item-1", "raw-json-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_Persists_CrawlResult_For_Each_Parsed_Episode()
    {
        var episode = MakeEpisode("item-2");
        var topicUrl = "https://api.ardmediathek.de/topics/ard";

        _client.FetchTopicUrlsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _client.FetchTopicUrlsAsync("ard", Arg.Any<CancellationToken>())
            .Returns([topicUrl]);
        _client.FetchTopicItemIdsAsync(topicUrl, Arg.Any<CancellationToken>())
            .Returns(["item-2"]);
        _client.FetchEpisodeAsync("item-2", Arg.Any<CancellationToken>())
            .Returns(("raw-json-2", episode));

        await _handler.HandleAsync(new CrawlArdFullCommand());

        // One German + one AD variant = 2 CrawlResults persisted
        await _repo.Received(2).UpsertAsync(Arg.Any<CrawlResult>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_Counts_Errors_Without_Throwing()
    {
        _client.FetchTopicUrlsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _client.FetchTopicUrlsAsync("ard", Arg.Any<CancellationToken>())
            .Returns(["https://topic.url"]);
        _client.FetchTopicItemIdsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(["bad-item"]);
        _client.FetchEpisodeAsync("bad-item", Arg.Any<CancellationToken>())
            .Returns(Task.FromException<(string, ArdEpisodeRaw)?>(new HttpRequestException("timeout")));

        var summary = await _handler.HandleAsync(new CrawlArdFullCommand());

        summary.Errors.ShouldBe(1);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ArdEpisodeRaw MakeEpisode(string itemId) => new(
        ItemId:         itemId,
        BroadcasterKey: "ARD",
        ShowTitle:      "Tagesschau",
        EpisodeTitle:   "Tagesschau 20 Uhr",
        Description:    null,
        BroadcastTime:  DateTimeOffset.UtcNow,
        Duration:       TimeSpan.FromMinutes(15),
        GeoBlocked:     false,
        Streams:        [new ArdStreamRaw(StreamQuality.Normal, "https://example.com/video.mp4")],
        StreamsAd:      [new ArdStreamRaw(StreamQuality.Normal, "https://example.com/ad.mp4")],
        StreamsDgs:     [],
        StreamsOv:      [],
        SubtitleUrls:   [],
        RelatedItemIds: []
    );

    private void SetupEmptyClients()
    {
        _client.FetchTopicUrlsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _client.FetchTopicItemIdsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([]);
    }
}
