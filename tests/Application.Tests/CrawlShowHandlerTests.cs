using System.Linq;
using Krautwatch.Application.Crawling;
using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Krautwatch.Application.Tests;

public class CrawlShowHandlerTests
{
    private sealed class FakeCrawler(string providerKey, IReadOnlyList<Episode> result) : IBroadcasterCrawler
    {
        public string ProviderKey => providerKey;
        public string? LastQuery { get; private set; }

        public Task<IReadOnlyList<Episode>> CrawlShowAsync(string showQuery, CancellationToken ct = default)
        {
            LastQuery = showQuery;
            return Task.FromResult(result);
        }
    }

    private static Episode Ep(string id) => new()
    {
        Id = id,
        Title = id,
        ShowId = "zdf:heute-show",
        BroadcastDate = DateTimeOffset.UtcNow,
        Duration = TimeSpan.FromMinutes(30),
    };

    [Fact]
    public async Task Handle_selects_crawler_by_provider_and_upserts_the_crawled_episodes()
    {
        var episodes = new[] { Ep("zdf:1"), Ep("zdf:2") };
        var zdf = new FakeCrawler("zdf", episodes);
        var otherProvider = new FakeCrawler("ard", []);
        var repo = Substitute.For<IEpisodeRepository>();

        var handler = new CrawlShowHandler([otherProvider, zdf], repo, NullLogger<CrawlShowHandler>.Instance);
        await handler.HandleAsync(new CrawlShowCommand("zdf", "heute-show"));

        zdf.LastQuery.ShouldBe("heute-show");
        otherProvider.LastQuery.ShouldBeNull(); // the ARD crawler must not be invoked
        await repo.Received(1).UpsertManyAsync(
            Arg.Is<IEnumerable<Episode>>(e => e != null && e.Count() == 2), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_is_case_insensitive_on_provider_key()
    {
        var zdf = new FakeCrawler("zdf", new[] { Ep("zdf:1") });
        var repo = Substitute.For<IEpisodeRepository>();

        var handler = new CrawlShowHandler([zdf], repo, NullLogger<CrawlShowHandler>.Instance);
        await handler.HandleAsync(new CrawlShowCommand("ZDF", "heute-show"));

        zdf.LastQuery.ShouldBe("heute-show");
        await repo.Received(1).UpsertManyAsync(Arg.Any<IEnumerable<Episode>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_unknown_provider_is_a_no_op()
    {
        var repo = Substitute.For<IEpisodeRepository>();

        var handler = new CrawlShowHandler([], repo, NullLogger<CrawlShowHandler>.Instance);
        await handler.HandleAsync(new CrawlShowCommand("kika", "Biene Maja"));

        await repo.DidNotReceive().UpsertManyAsync(Arg.Any<IEnumerable<Episode>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_empty_crawl_result_does_not_upsert()
    {
        var ard = new FakeCrawler("ard", []);
        var repo = Substitute.For<IEpisodeRepository>();

        var handler = new CrawlShowHandler([ard], repo, NullLogger<CrawlShowHandler>.Instance);
        await handler.HandleAsync(new CrawlShowCommand("ard", "Nonexistent Show"));

        await repo.DidNotReceive().UpsertManyAsync(Arg.Any<IEnumerable<Episode>>(), Arg.Any<CancellationToken>());
    }
}
