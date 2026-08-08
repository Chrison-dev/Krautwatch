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

    [Fact]
    public async Task Testing_an_edit_with_no_key_borrows_the_stored_one()
    {
        // #66: the edit form is told "leave blank to keep the current key", so a blank key must test with
        // the stored key rather than refusing — the operator was never shown it to re-type.
        var repo = Substitute.For<IArrInstanceRepository>();
        var client = Substitute.For<IArrClient>();
        var stored = Instance();
        repo.GetByIdAsync(stored.Id, Arg.Any<CancellationToken>()).Returns(stored);
        client.TestConnectionAsync(Arg.Any<ArrInstance>(), Arg.Any<CancellationToken>())
            .Returns(ArrConnectionResult.Success("Sonarr", "4.0.10"));

        // The form's draft: the URL has been edited, and the key field left blank.
        var draft = new ArrInstance
        {
            Name = stored.Name, Kind = stored.Kind, BaseUrl = "http://sonarr:9999", ApiKey = "",
        };

        var result = await new TestArrConnectionHandler(repo, client)
            .HandleAsync(stored.Id, draft, TestContext.Current.CancellationToken);

        result.Ok.ShouldBeTrue();

        // The edited URL is what gets tested — testing the stored record would silently ignore the change.
        await client.Received(1).TestConnectionAsync(
            Arg.Is<ArrInstance>(i => i != null && i.BaseUrl == "http://sonarr:9999" && i.ApiKey == "key-abc"),
            Arg.Any<CancellationToken>());

        // Still a dry run.
        await repo.DidNotReceive().UpdateAsync(Arg.Any<ArrInstance>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Testing_an_edit_of_a_vanished_instance_reports_it_rather_than_calling_out()
    {
        var repo = Substitute.For<IArrInstanceRepository>();
        var client = Substitute.For<IArrClient>();
        repo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((ArrInstance?)null);

        var result = await new TestArrConnectionHandler(repo, client).HandleAsync(
            Guid.NewGuid(),
            new ArrInstance { Name = "Sonarr", BaseUrl = "http://sonarr:8989", ApiKey = "" },
            TestContext.Current.CancellationToken);

        result.Ok.ShouldBeFalse();

        // With no stored key to borrow, calling out would authenticate with an empty string and report a
        // confusing 401 instead of the real cause.
        await client.DidNotReceive().TestConnectionAsync(
            Arg.Any<ArrInstance>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Testing_passes_the_stored_form_through_so_a_reference_resolves_at_the_client()
    {
        // Resolution belongs at the point of use, so the handler must hand the *pointer* to the client
        // rather than resolving it itself — see docs/plans/2026-08-09 - secret-handling.md.
        var repo = Substitute.For<IArrInstanceRepository>();
        var client = Substitute.For<IArrClient>();
        var stored = Instance();
        stored.ApiKey = "env:SONARR_API_KEY";
        repo.GetByIdAsync(stored.Id, Arg.Any<CancellationToken>()).Returns(stored);
        client.TestConnectionAsync(Arg.Any<ArrInstance>(), Arg.Any<CancellationToken>())
            .Returns(ArrConnectionResult.Fail(
                ArrConnectionFailure.SecretUnresolved,
                "Environment variable SONARR_API_KEY is not set in this container."));

        var result = await new TestArrConnectionHandler(repo, client)
            .HandleAsync(stored.Id, TestContext.Current.CancellationToken);

        result.Ok.ShouldBeFalse();
        result.Failure.ShouldBe("SecretUnresolved");   // distinct from Unauthorized, so the UI can explain
        result.Message.ShouldContain("SONARR_API_KEY");

        await client.Received(1).TestConnectionAsync(
            Arg.Is<ArrInstance>(i => i != null && i.ApiKey == "env:SONARR_API_KEY"),
            Arg.Any<CancellationToken>());
    }
}
