using Krautwatch.Application.Settings;
using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Enums;
using Krautwatch.Domain.Interfaces;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Krautwatch.Application.Tests;

public class TestArrConnectionHandlerTests
{
    private static ArrInstance Instance() => new()
    {
        Id = Guid.NewGuid(), Name = "Sonarr", Kind = ArrKind.Sonarr,
        BaseUrl = "http://sonarr:8989", ApiKey = "key-abc",
    };

    [Fact]
    public async Task Caches_a_successful_outcome_on_the_instance()
    {
        var repo = Substitute.For<IArrInstanceRepository>();
        var client = Substitute.For<IArrClient>();
        var instance = Instance();
        repo.GetByIdAsync(instance.Id, Arg.Any<CancellationToken>()).Returns(instance);
        client.TestConnectionAsync(instance, Arg.Any<CancellationToken>())
            .Returns(ArrConnectionResult.Success("Sonarr", "4.0.10"));

        var result = await new TestArrConnectionHandler(repo, client)
            .HandleAsync(instance.Id, TestContext.Current.CancellationToken);

        result.Ok.ShouldBeTrue();
        result.Message.ShouldBe("Sonarr 4.0.10");
        result.Failure.ShouldBeNull();

        // Cached so the settings page can render state without re-probing every instance.
        instance.LastTestOk.ShouldBe(true);
        instance.LastTestMessage.ShouldBe("Sonarr 4.0.10");
        instance.LastTestedAt.ShouldNotBeNull();
        await repo.Received(1).UpdateAsync(instance, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Caches_a_failure_with_its_specific_cause()
    {
        var repo = Substitute.For<IArrInstanceRepository>();
        var client = Substitute.For<IArrClient>();
        var instance = Instance();
        repo.GetByIdAsync(instance.Id, Arg.Any<CancellationToken>()).Returns(instance);
        client.TestConnectionAsync(instance, Arg.Any<CancellationToken>()).Returns(
            ArrConnectionResult.Fail(ArrConnectionFailure.Unauthorized, "API key was rejected."));

        var result = await new TestArrConnectionHandler(repo, client)
            .HandleAsync(instance.Id, TestContext.Current.CancellationToken);

        result.Ok.ShouldBeFalse();
        result.Failure.ShouldBe("Unauthorized"); // specific, so the UI can say something useful
        instance.LastTestOk.ShouldBe(false);
        await repo.Received(1).UpdateAsync(instance, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reports_a_missing_instance_without_calling_out()
    {
        var repo = Substitute.For<IArrInstanceRepository>();
        var client = Substitute.For<IArrClient>();
        repo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((ArrInstance?)null);

        var result = await new TestArrConnectionHandler(repo, client)
            .HandleAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.Ok.ShouldBeFalse();
        await client.DidNotReceive().TestConnectionAsync(Arg.Any<ArrInstance>(), Arg.Any<CancellationToken>());
        await repo.DidNotReceive().UpdateAsync(Arg.Any<ArrInstance>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Testing_unsaved_details_writes_nothing()
    {
        // So a wrong key never has to be persisted just to find out it is wrong.
        var repo = Substitute.For<IArrInstanceRepository>();
        var client = Substitute.For<IArrClient>();
        var unsaved = Instance();
        client.TestConnectionAsync(unsaved, Arg.Any<CancellationToken>())
            .Returns(ArrConnectionResult.Success("Radarr", "5.2"));

        var result = await new TestArrConnectionHandler(repo, client)
            .HandleAsync(unsaved, TestContext.Current.CancellationToken);

        result.Ok.ShouldBeTrue();
        result.Message.ShouldBe("Radarr 5.2");
        await repo.DidNotReceive().UpdateAsync(Arg.Any<ArrInstance>(), Arg.Any<CancellationToken>());
        await repo.DidNotReceive().AddAsync(Arg.Any<ArrInstance>(), Arg.Any<CancellationToken>());
    }
}
