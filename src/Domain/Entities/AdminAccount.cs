namespace Krautwatch.Domain.Entities;

/// <summary>
/// The single local administrator account, used when <c>Auth:Provider = local</c>. Singleton row —
/// always Id = 1, like <see cref="AppSettings"/>.
/// </summary>
/// <remarks>
/// Krautwatch deliberately has no user system: one admin covers the self-hosted audience, and anyone
/// wanting real identity management points <c>Auth:Provider</c> at their own OIDC provider instead.
/// <see cref="PasswordHash"/> is a versioned PBKDF2 hash produced by the password-hasher port — never
/// a raw or reversible value.
/// </remarks>
public class AdminAccount
{
    public int Id { get; set; } = 1;

    public string Username { get; set; } = string.Empty;

    /// <summary>Versioned salted hash. Never contains the password, and is never returned to the UI.</summary>
    public string PasswordHash { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastLoginAt { get; set; }
}
