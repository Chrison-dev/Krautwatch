using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Interfaces;

namespace Krautwatch.Application.Settings;

// ══════════════════════════════════════════════════════════════
// Messages
// ══════════════════════════════════════════════════════════════

/// <summary>Result of a connectivity test, shaped for display.</summary>
public record ArrConnectionTestResult(bool Ok, string Message, string? Failure)
{
    public static ArrConnectionTestResult From(ArrConnectionResult result) => new(
        result.Ok,
        result.Message,
        result.Ok ? null : result.Failure.ToString());

    public static ArrConnectionTestResult NotFound() =>
        new(false, "That instance no longer exists.", nameof(ArrConnectionFailure.Unexpected));
}

// ══════════════════════════════════════════════════════════════
// Action (IO-driven, DR-009)
// ══════════════════════════════════════════════════════════════

/// <summary>
/// Tests connectivity to a configured Sonarr/Radarr instance and caches the outcome on the record, so the
/// settings page can show state without re-probing every instance on each page load.
/// </summary>
/// <remarks>
/// This is an **Action** — it touches the outside world — but unlike the crawl Actions it runs in a UI
/// host rather than an agent. That is a deliberate, narrow deviation from DR-009: the operator clicks a
/// button and expects an answer, and round-tripping "test this instance" through the durable bus to an
/// agent and polling for a result is far more machinery than a button warrants. Recorded in
/// <c>docs/plans/2026-07-27 - arr-instance-config-ui.md</c>.
/// </remarks>
public class TestArrConnectionHandler(IArrInstanceRepository repository, IArrClient client)
{
    /// <summary>Tests a stored instance and persists the outcome.</summary>
    public async Task<ArrConnectionTestResult> HandleAsync(Guid instanceId, CancellationToken ct = default)
    {
        var instance = await repository.GetByIdAsync(instanceId, ct);
        if (instance is null)
            return ArrConnectionTestResult.NotFound();

        var result = await client.TestConnectionAsync(instance, ct);

        instance.LastTestedAt = DateTimeOffset.UtcNow;
        instance.LastTestOk = result.Ok;
        instance.LastTestMessage = result.Message;
        await repository.UpdateAsync(instance, ct);

        return ArrConnectionTestResult.From(result);
    }

    /// <summary>
    /// Tests unsaved details, so the operator can verify before committing a record — and so a wrong key
    /// never has to be persisted just to discover it is wrong. Nothing is written.
    /// </summary>
    public async Task<ArrConnectionTestResult> HandleAsync(
        ArrInstance unsaved,
        CancellationToken ct = default) =>
        ArrConnectionTestResult.From(await client.TestConnectionAsync(unsaved, ct));

    /// <summary>
    /// Tests unsaved edits to an instance that <em>already exists</em>, filling the API key in from storage.
    /// Nothing is written — this is still a dry run.
    /// </summary>
    /// <remarks>
    /// The edit form never receives the real key (it is masked on the read model), so a blank key field
    /// means "keep the stored one". Without this overload the operator would have to re-type a credential
    /// they were deliberately never shown, just to test a changed URL — the contradiction in #66. Resolving
    /// the key server-side keeps the masking guarantee intact: it still never reaches the browser.
    /// </remarks>
    public async Task<ArrConnectionTestResult> HandleAsync(
        Guid instanceId,
        ArrInstance unsaved,
        CancellationToken ct = default)
    {
        var stored = await repository.GetByIdAsync(instanceId, ct);
        if (stored is null)
            return ArrConnectionTestResult.NotFound();

        // `unsaved` is a throwaway built from the form, so borrowing the stored credential onto it is safe
        // — and it keeps the operator's edited URL/name, which testing the stored record would discard.
        unsaved.ApiKey = stored.ApiKey;

        return ArrConnectionTestResult.From(await client.TestConnectionAsync(unsaved, ct));
    }
}
