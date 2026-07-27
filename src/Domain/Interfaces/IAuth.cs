using Krautwatch.Domain.Entities;

namespace Krautwatch.Domain.Interfaces;

/// <summary>
/// Persistence for the single local administrator account (<c>Auth:Provider = local</c>).
/// </summary>
/// <remarks>
/// Deliberately framework-agnostic: Domain knows nothing about cookies, claims or ASP.NET. The OIDC
/// provider has no counterpart here on purpose — OIDC is a redirect/token protocol handled entirely by
/// the framework's OpenIdConnect middleware in Presentation, so there is nothing for a Domain port to
/// abstract. Both providers converge on the same authenticated principal instead. See
/// <c>docs/plans/2026-07-27 - authentication.md</c>.
/// </remarks>
public interface ILocalCredentialStore
{
    /// <summary>True once an administrator exists — the signal that first-run setup is complete.</summary>
    Task<bool> ExistsAsync(CancellationToken ct = default);

    Task<AdminAccount?> GetAsync(CancellationToken ct = default);

    /// <summary>
    /// Creates the administrator. Fails if one already exists, so first-run setup cannot be replayed to
    /// take over an instance.
    /// </summary>
    Task CreateAsync(AdminAccount account, CancellationToken ct = default);

    Task UpdateAsync(AdminAccount account, CancellationToken ct = default);
}

/// <summary>
/// Password hashing behind a port so Domain/Application never reference a hashing implementation.
/// </summary>
public interface IPasswordHasher
{
    /// <summary>Hashes a plaintext password into a versioned, salted representation.</summary>
    string Hash(string password);

    /// <summary>Verifies a password against a stored hash.</summary>
    PasswordVerification Verify(string hash, string password);
}

/// <summary>Outcome of a password check.</summary>
public enum PasswordVerification
{
    Failed = 0,
    Success = 1,

    /// <summary>Correct password, but stored with outdated parameters — rehash and persist.</summary>
    SuccessRehashNeeded = 2,
}
