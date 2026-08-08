using Krautwatch.Application.Settings;
using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Enums;
using Krautwatch.Domain.Interfaces;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Krautwatch.Application.Tests;

/// <summary>
/// Guards the sharpest hazard in the secret-reference design
/// (<c>docs/plans/2026-08-09 - secret-handling.md</c>): a stored reference must never be rewritten as the
/// resolved literal, which would silently convert a properly-managed secret back into stored plaintext.
/// </summary>
public class SecretReferenceRoundTripTests
{
    private static ArrInstance StoredWithReference() => new()
    {
        Id = Guid.NewGuid(), Name = "Sonarr", Kind = ArrKind.Sonarr,
        BaseUrl = "http://sonarr:8989", ApiKey = "env:SONARR_API_KEY",
    };

    [Fact]
    public async Task Saving_an_edit_with_a_blank_key_leaves_the_reference_intact()
    {
        var repo = Substitute.For<IArrInstanceRepository>();
        var stored = StoredWithReference();
        repo.GetByIdAsync(stored.Id, Arg.Any<CancellationToken>()).Returns(stored);

        // The operator renames the instance and leaves the key field blank, as the placeholder invites.
        await new SaveArrInstanceHandler(repo).HandleAsync(
            new SaveArrInstanceRequest(stored.Id, "Sonarr (4K)", ArrKind.Sonarr,
                "http://sonarr:8989", ApiKey: null, Enabled: true),
            TestContext.Current.CancellationToken);

        stored.Name.ShouldBe("Sonarr (4K)");
        stored.ApiKey.ShouldBe("env:SONARR_API_KEY");   // still a pointer, not a resolved secret
    }

    [Fact]
    public void The_read_model_never_carries_a_resolved_secret()
    {
        // If the response echoed the resolved value, an edit round-trip could persist it as a literal.
        var secrets = Substitute.For<ISecretResolver>();
        secrets.Resolve("env:SONARR_API_KEY")
            .Returns(SecretResolution.From("the-real-secret", SecretOrigin.Environment));

        var response = GetResponse(StoredWithReference(), secrets);

        response.ApiKeyMasked.ShouldNotContain("the-real-secret");
        response.ApiKeyMasked.ShouldBe("env:SONARR_API_KEY");
        response.ApiKeyIsReference.ShouldBeTrue();
        response.ApiKeyProblem.ShouldBeNull();
    }

    [Fact]
    public void A_reference_is_shown_verbatim_because_a_pointer_is_not_a_credential()
    {
        // Masking it would hide the one thing the operator came to check: which variable is wired.
        var secrets = Substitute.For<ISecretResolver>();
        secrets.Resolve(Arg.Any<string>())
            .Returns(SecretResolution.From("x", SecretOrigin.Environment));

        GetResponse(StoredWithReference(), secrets).ApiKeyMasked.ShouldBe("env:SONARR_API_KEY");
    }

    [Fact]
    public void A_literal_key_is_still_masked()
    {
        var secrets = Substitute.For<ISecretResolver>();
        var instance = StoredWithReference();
        instance.ApiKey = "abcdef123456";

        var response = GetResponse(instance, secrets);

        response.ApiKeyMasked.ShouldBe("••••3456");
        response.ApiKeyIsReference.ShouldBeFalse();

        // A literal needs no resolution, so the read path must not probe the environment for it.
        secrets.DidNotReceive().Resolve(Arg.Any<string>());
    }

    [Fact]
    public void An_unresolvable_reference_is_reported_on_the_row()
    {
        var secrets = Substitute.For<ISecretResolver>();
        secrets.Resolve("env:SONARR_API_KEY")
            .Returns(SecretResolution.Unresolved("Environment variable SONARR_API_KEY is not set."));

        var response = GetResponse(StoredWithReference(), secrets);

        response.ApiKeyProblem.ShouldNotBeNull().ShouldContain("SONARR_API_KEY");
    }

    private static ArrInstanceResponse GetResponse(ArrInstance instance, ISecretResolver secrets)
    {
        var repo = Substitute.For<IArrInstanceRepository>();
        repo.GetAllAsync(Arg.Any<CancellationToken>()).Returns([instance]);

        return new GetArrInstancesHandler(repo, secrets)
            .HandleAsync(CancellationToken.None).GetAwaiter().GetResult().Single();
    }
}
