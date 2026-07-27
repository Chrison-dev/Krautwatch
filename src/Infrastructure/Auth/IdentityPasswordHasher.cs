using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Interfaces;
using Microsoft.AspNetCore.Identity;

namespace Krautwatch.Infrastructure.Auth;

/// <summary>
/// Adapts ASP.NET Core Identity's <see cref="PasswordHasher{TUser}"/> to the
/// <see cref="IPasswordHasher"/> port: PBKDF2-HMAC-SHA256 with a per-password salt and a versioned
/// hash format that supports rehash-on-verify.
/// </summary>
/// <remarks>
/// We take the hasher only — not the Identity user/role stack, which Krautwatch has no use for. This is
/// deliberately delegated rather than hand-rolled; the one thing worse than a boring password hash is a
/// bespoke one.
/// </remarks>
public class IdentityPasswordHasher : IPasswordHasher
{
    // The generic parameter is unused by the PBKDF2 implementation — AdminAccount just satisfies it.
    private readonly PasswordHasher<AdminAccount> _inner = new();
    private static readonly AdminAccount Unused = new();

    public string Hash(string password) => _inner.HashPassword(Unused, password);

    public PasswordVerification Verify(string hash, string password) =>
        _inner.VerifyHashedPassword(Unused, hash, password) switch
        {
            PasswordVerificationResult.Success => PasswordVerification.Success,
            PasswordVerificationResult.SuccessRehashNeeded => PasswordVerification.SuccessRehashNeeded,
            _ => PasswordVerification.Failed,
        };
}
