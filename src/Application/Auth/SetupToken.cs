using System.Security.Cryptography;

namespace Krautwatch.Application.Auth;

/// <summary>
/// Guards first-run administrator creation with a token generated at startup and written to the host
/// log, so <c>/setup</c> cannot be claimed by whoever reaches the instance first.
/// </summary>
/// <remarks>
/// Registered as a singleton, so the token lives for the process lifetime and rotates on restart — the
/// operator reads the current one from the log. It is intentionally not persisted: there is nothing to
/// leak at rest, and a restart invalidating a half-finished setup link is the safe direction.
/// <para>
/// Leaving setup open until claimed (what most self-hosted apps do) is an admin-takeover window: on a
/// shared network the first visitor owns the instance. See
/// <c>docs/plans/2026-07-27 - authentication.md</c>.
/// </para>
/// </remarks>
public sealed class SetupToken
{
    public SetupToken()
    {
        // 160 bits, URL-safe — long enough that guessing is not a consideration.
        Value = Convert.ToBase64String(RandomNumberGenerator.GetBytes(20))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    public string Value { get; }

    /// <summary>
    /// Constant-time comparison of a supplied token against the expected one, so a mismatch cannot be
    /// narrowed down by timing.
    /// </summary>
    public bool Matches(string? supplied) =>
        !string.IsNullOrEmpty(supplied)
        && CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(supplied),
            System.Text.Encoding.UTF8.GetBytes(Value));
}
