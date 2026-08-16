using Krautwatch.Domain.Entities;
using Krautwatch.Infrastructure.Catalog;
using Krautwatch.Infrastructure.Persistence;
using Shouldly;
using Xunit;

namespace Krautwatch.Infrastructure.Tests;

/// <summary>
/// Paging the recent-releases feed (#12). Against real Postgres, because the thing being tested is
/// what the database does with <c>OFFSET</c> over an ordering that has ties — which an in-memory
/// stand-in would answer differently, and more forgivingly, than the real thing.
/// </summary>
[Collection(PostgresCollection.Name)]
public class RecentPagingTests(PostgresFixture postgres) : IAsyncLifetime
{
    private AppDbContext _db = null!;
    private EpisodeRepository _sut = null!;

    /// <summary>All on the same instant, which is the case the tiebreaker exists for.</summary>
    private static readonly DateTimeOffset SharedBroadcast = new(2026, 7, 1, 20, 15, 0, TimeSpan.Zero);

    public async ValueTask InitializeAsync()
    {
        _db = new AppDbContext(await postgres.CreateDatabaseAsync());
        _sut = new EpisodeRepository(_db);

        var channel = new Channel { Id = "zdf", Name = "ZDF", ProviderKey = "zdf" };
        var show = new Show { Id = "zdf:heute-show", Title = "heute-show", ChannelId = "zdf", Channel = channel };

        // Twenty episodes sharing one broadcast date. Real catalogs do this constantly — a batch crawled
        // from one show, and every episode whose air date could not be parsed sharing MinValue.
        for (var i = 0; i < 20; i++)
        {
            _db.Add(new Episode
            {
                Id = $"zdf:{i:D2}",
                Title = $"Episode {i}",
                ShowId = show.Id,
                Show = show,
                BroadcastDate = SharedBroadcast,
                Duration = TimeSpan.FromMinutes(30),
            });
        }

        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        _db.ChangeTracker.Clear();
    }

    public ValueTask DisposeAsync()
    {
        _db.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Paging_through_tied_broadcast_dates_returns_every_episode_exactly_once()
    {
        var seen = new List<string>();

        for (var offset = 0; offset < 20; offset += 5)
        {
            var page = await _sut.GetRecentAsync(offset, 5, TestContext.Current.CancellationToken);
            seen.AddRange(page.Select(e => e.Id));
        }

        // Without a tiebreaker in the ORDER BY, Postgres is free to order the tied rows differently per
        // query — so a client walking the pages sees some episodes twice and misses others entirely.
        // That is the silent half of the catch-up bug: not an error, just a gap in what got grabbed.
        seen.Count.ShouldBe(20);
        seen.Distinct().Count().ShouldBe(20);
    }

    [Fact]
    public async Task The_same_page_asked_for_twice_is_the_same_page()
    {
        var first = await _sut.GetRecentAsync(5, 5, TestContext.Current.CancellationToken);
        var again = await _sut.GetRecentAsync(5, 5, TestContext.Current.CancellationToken);

        again.Select(e => e.Id).ShouldBe(first.Select(e => e.Id));
    }

    [Fact]
    public async Task Count_is_what_the_feed_reports_as_the_total()
    {
        (await _sut.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(20);
    }

    [Fact]
    public async Task Recency_still_wins_over_the_tiebreaker()
    {
        _db.Add(new Episode
        {
            Id = "zdf:aaa-newest",   // sorts last by id, so only recency can put it first
            Title = "Newest",
            ShowId = "zdf:heute-show",
            BroadcastDate = SharedBroadcast.AddDays(1),
            Duration = TimeSpan.FromMinutes(30),
        });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var page = await _sut.GetRecentAsync(0, 1, TestContext.Current.CancellationToken);

        page.ShouldHaveSingleItem().Id.ShouldBe("zdf:aaa-newest");
    }
}
