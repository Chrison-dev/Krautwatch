using FluentValidation;
using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Interfaces;

namespace Krautwatch.Application.Auth;

// ══════════════════════════════════════════════════════════════
// Messages
// ══════════════════════════════════════════════════════════════

public record SignInRequest(string Username, string Password);

/// <summary>
/// Sign-in outcome. Deliberately coarse — the UI must not distinguish "no such user" from "wrong
/// password", since that turns the login form into a username oracle.
/// </summary>
public record SignInResult(bool Succeeded, string? Username)
{
    public static SignInResult Fail() => new(false, null);
    public static SignInResult Ok(string username) => new(true, username);
}

public record CreateAdminRequest(string Username, string Password, string ConfirmPassword);

// ══════════════════════════════════════════════════════════════
// Validators
// ══════════════════════════════════════════════════════════════

public class CreateAdminRequestValidator : AbstractValidator<CreateAdminRequest>
{
    public CreateAdminRequestValidator()
    {
        RuleFor(x => x.Username)
            .NotEmpty().WithMessage("Username must not be empty.")
            .MaximumLength(100).WithMessage("Username must be 100 characters or fewer.");

        // No minimum length, and no character-class rules. This is a single-admin credential on a
        // self-hosted box the operator already controls — Sonarr and Radarr impose nothing either, and a
        // policy that blocks the password someone actually wanted just moves it onto a sticky note. The
        // upper bound stays: it is a storage bound, not an opinion. Operators wanting real password policy
        // should use `Auth:Provider = oidc` and enforce it at the identity provider.
        RuleFor(x => x.Password)
            .NotEmpty().WithMessage("Password must not be empty.")
            .MaximumLength(256).WithMessage("Password must be 256 characters or fewer.");

        RuleFor(x => x.ConfirmPassword)
            .Equal(x => x.Password).WithMessage("Passwords do not match.");
    }
}

// ══════════════════════════════════════════════════════════════
// Handlers
// ══════════════════════════════════════════════════════════════

/// <summary>Verifies local credentials. Does not issue a cookie — that is the host's concern.</summary>
public class SignInHandler(ILocalCredentialStore store, IPasswordHasher hasher)
{
    public async Task<SignInResult> HandleAsync(SignInRequest request, CancellationToken ct = default)
    {
        var admin = await store.GetAsync(ct);
        if (admin is null)
            return SignInResult.Fail();

        if (!string.Equals(admin.Username, request.Username, StringComparison.OrdinalIgnoreCase))
            return SignInResult.Fail();

        var verification = hasher.Verify(admin.PasswordHash, request.Password);
        if (verification == PasswordVerification.Failed)
            return SignInResult.Fail();

        if (verification == PasswordVerification.SuccessRehashNeeded)
            admin.PasswordHash = hasher.Hash(request.Password);

        admin.LastLoginAt = DateTimeOffset.UtcNow;
        await store.UpdateAsync(admin, ct);

        return SignInResult.Ok(admin.Username);
    }
}

/// <summary>
/// Creates the administrator during first-run setup. Refuses if one already exists, so a replayed
/// request cannot take over a configured instance.
/// </summary>
public class CreateAdminHandler(ILocalCredentialStore store, IPasswordHasher hasher)
{
    public async Task<bool> HandleAsync(CreateAdminRequest request, CancellationToken ct = default)
    {
        if (await store.ExistsAsync(ct))
            return false;

        await store.CreateAsync(new AdminAccount
        {
            Username = request.Username,
            PasswordHash = hasher.Hash(request.Password),
            CreatedAt = DateTimeOffset.UtcNow,
        }, ct);

        return true;
    }
}

/// <summary>Whether first-run setup is still pending — drives both the /setup gate and the startup log.</summary>
public class SetupStateHandler(ILocalCredentialStore store)
{
    public async Task<bool> IsSetupRequiredAsync(CancellationToken ct = default) =>
        !await store.ExistsAsync(ct);
}
