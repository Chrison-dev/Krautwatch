using Krautwatch.Domain.Interfaces;

namespace Krautwatch.Application.Settings;

// ══════════════════════════════════════════════════════════════
// Messages
// ══════════════════════════════════════════════════════════════

/// <summary>The steps of first-run setup, in order (#54).</summary>
public enum SetupStep
{
    /// <summary>What Krautwatch is, and that Sonarr/Radarr drive it (DR-010).</summary>
    Welcome = 0,

    /// <summary>Administrator account — the existing token-gated <c>/setup</c> form.</summary>
    Administrator = 1,

    /// <summary>Resolved database and whether the schema is applied. Read-only.</summary>
    Database = 2,

    /// <summary>Download directory and parallelism.</summary>
    Downloads = 3,

    /// <summary>Geo-restricted egress — how DACH-only assets are reached.</summary>
    Egress = 4,

    /// <summary>Sonarr/Radarr instances, plus what to paste back into them.</summary>
    ArrInstances = 5,

    /// <summary>Summary.</summary>
    Done = 6,
}

/// <summary>Where first-run setup stands.</summary>
/// <param name="Required">True while the wizard has not been completed.</param>
/// <param name="ResumeAt">The first step still worth showing.</param>
public record SetupState(bool Required, SetupStep ResumeAt, bool AdministratorExists)
{
    public static SetupState Complete() => new(false, SetupStep.Done, true);
}

// ══════════════════════════════════════════════════════════════
// Handlers
// ══════════════════════════════════════════════════════════════

/// <summary>
/// Decides whether first-run setup is still needed, and where to resume it (#54).
/// </summary>
/// <remarks>
/// Completion is its own stored timestamp rather than "an administrator exists". Inferring it from the
/// admin account cannot express <em>admin created, wizard abandoned halfway</em>, so an interrupted setup
/// would restart from the beginning instead of resuming — which #54 asks for explicitly.
/// </remarks>
public class SetupWizardStateHandler(ISettingsRepository settings, ILocalCredentialStore credentials)
{
    public async Task<SetupState> HandleAsync(CancellationToken ct = default)
    {
        var stored = await settings.GetAsync(ct);
        if (stored.SetupCompletedAt is not null)
            return SetupState.Complete();

        var adminExists = await credentials.ExistsAsync(ct);

        // Without an administrator the only reachable step is creating one — every later step requires
        // the session that creating it produces.
        return new SetupState(
            Required: true,
            ResumeAt: adminExists ? SetupStep.Database : SetupStep.Welcome,
            AdministratorExists: adminExists);
    }
}

/// <summary>Marks first-run setup finished, so the wizard never triggers again.</summary>
public class CompleteSetupHandler(ISettingsRepository settings)
{
    public async Task HandleAsync(CancellationToken ct = default)
    {
        var stored = await settings.GetAsync(ct);

        // Idempotent: finishing twice (a double submit, a refresh of the last step) must not move the
        // timestamp, which is a record of when this instance was set up.
        if (stored.SetupCompletedAt is not null)
            return;

        stored.SetupCompletedAt = DateTimeOffset.UtcNow;
        await settings.SaveAsync(stored, ct);
    }
}
