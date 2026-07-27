using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Enums;
using Krautwatch.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace Krautwatch.Infrastructure.Tests;

[Collection(PostgresCollection.Name)]
public class ArrInstanceRepositoryTests(PostgresFixture postgres) : IAsyncLifetime
{
    private DbContextOptions<AppDbContext> _options = null!;

    public async ValueTask InitializeAsync() => _options = await postgres.CreateDatabaseAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private ArrInstanceRepository Repo() => new(new AppDbContext(_options));

    private static ArrInstance Instance(
        string name = "Sonarr",
        ArrKind kind = ArrKind.Sonarr,
        string baseUrl = "http://sonarr:8989",
        bool enabled = true) => new()
        {
            Name = name,
            Kind = kind,
            BaseUrl = baseUrl,
            ApiKey = "key-abc123",
            Enabled = enabled,
        };

    [Fact]
    public async Task Adds_then_reads_back_an_instance()
    {
        var instance = Instance();
        await Repo().AddAsync(instance, TestContext.Current.CancellationToken);

        var found = await Repo().GetByIdAsync(instance.Id, TestContext.Current.CancellationToken);

        found.ShouldNotBeNull();
        found.Name.ShouldBe("Sonarr");
        found.Kind.ShouldBe(ArrKind.Sonarr);
        found.BaseUrl.ShouldBe("http://sonarr:8989");
        found.ApiKey.ShouldBe("key-abc123");
        found.Enabled.ShouldBeTrue();
    }

    [Fact]
    public async Task Kind_round_trips_as_text()
    {
        // Stored as text rather than an int so the column stays readable in the database.
        await Repo().AddAsync(Instance(kind: ArrKind.Radarr, baseUrl: "http://radarr:7878"),
            TestContext.Current.CancellationToken);

        var all = await Repo().GetAllAsync(TestContext.Current.CancellationToken);

        all.ShouldHaveSingleItem().Kind.ShouldBe(ArrKind.Radarr);
    }

    [Fact]
    public async Task Duplicate_base_urls_are_rejected_by_the_schema()
    {
        // #5 bootstraps instances from env vars and matches on base URL, so duplicates must be
        // impossible at the schema level rather than prevented by convention.
        await Repo().AddAsync(Instance(baseUrl: "http://sonarr:8989"), TestContext.Current.CancellationToken);

        await Should.ThrowAsync<DbUpdateException>(async () =>
            await Repo().AddAsync(
                Instance(name: "Duplicate", baseUrl: "http://sonarr:8989"),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetEnabled_skips_disabled_instances()
    {
        await Repo().AddAsync(Instance(name: "On", baseUrl: "http://on:8989"),
            TestContext.Current.CancellationToken);
        await Repo().AddAsync(Instance(name: "Off", baseUrl: "http://off:8989", enabled: false),
            TestContext.Current.CancellationToken);

        var enabled = await Repo().GetEnabledAsync(TestContext.Current.CancellationToken);

        enabled.ShouldHaveSingleItem().Name.ShouldBe("On");
        (await Repo().GetAllAsync(TestContext.Current.CancellationToken)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task Updates_the_cached_test_outcome()
    {
        var instance = Instance();
        await Repo().AddAsync(instance, TestContext.Current.CancellationToken);

        var loaded = await Repo().GetByIdAsync(instance.Id, TestContext.Current.CancellationToken);
        loaded!.LastTestOk = true;
        loaded.LastTestMessage = "Sonarr 4.0.10";
        loaded.LastTestedAt = DateTimeOffset.UtcNow;
        await Repo().UpdateAsync(loaded, TestContext.Current.CancellationToken);

        var reread = await Repo().GetByIdAsync(instance.Id, TestContext.Current.CancellationToken);
        reread!.LastTestOk.ShouldBe(true);
        reread.LastTestMessage.ShouldBe("Sonarr 4.0.10");
        reread.LastTestedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Deletes_an_instance()
    {
        var instance = Instance();
        await Repo().AddAsync(instance, TestContext.Current.CancellationToken);

        await Repo().DeleteAsync(instance.Id, TestContext.Current.CancellationToken);

        (await Repo().GetByIdAsync(instance.Id, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task Deleting_something_absent_is_a_no_op()
    {
        await Repo().DeleteAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        (await Repo().GetAllAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Orders_by_kind_then_name()
    {
        await Repo().AddAsync(Instance(name: "Zeta", ArrKind.Sonarr, "http://z:8989"),
            TestContext.Current.CancellationToken);
        await Repo().AddAsync(Instance(name: "Movies", ArrKind.Radarr, "http://m:7878"),
            TestContext.Current.CancellationToken);
        await Repo().AddAsync(Instance(name: "Alpha", ArrKind.Sonarr, "http://a:8989"),
            TestContext.Current.CancellationToken);

        var all = await Repo().GetAllAsync(TestContext.Current.CancellationToken);

        // Grouped by kind, then alphabetical within a kind. Kind is persisted as TEXT, so SQL sorts it
        // alphabetically ("Radarr" < "Sonarr") rather than by enum value — asserted here so the
        // behaviour is pinned rather than assumed.
        all.Select(i => i.Name).ShouldBe(["Movies", "Alpha", "Zeta"]);
    }
}
