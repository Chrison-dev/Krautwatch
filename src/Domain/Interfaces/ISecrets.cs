namespace Krautwatch.Domain.Interfaces;

/// <summary>Where a stored credential's value actually came from.</summary>
public enum SecretOrigin
{
    /// <summary>Nothing stored.</summary>
    Empty = 0,

    /// <summary>The stored text <em>is</em> the secret — plaintext in the database.</summary>
    Literal = 1,

    /// <summary>Read from an environment variable.</summary>
    Environment = 2,

    /// <summary>Read from a file on disk, typically a mounted secret.</summary>
    File = 3,

    /// <summary>A reference that could not be resolved <em>in this process</em>.</summary>
    Unresolved = 4,
}

/// <summary>
/// A stored credential resolved to its actual value, plus where that value came from.
/// </summary>
/// <remarks>
/// <see cref="Problem"/> is set only for <see cref="SecretOrigin.Unresolved"/>, and never contains the
/// secret — it names the missing variable or unreadable path so the operator can fix it.
/// </remarks>
public record SecretResolution(string? Value, SecretOrigin Origin, string? Problem = null)
{
    public static SecretResolution Empty() => new(null, SecretOrigin.Empty);

    public static SecretResolution Literal(string value) => new(value, SecretOrigin.Literal);

    public static SecretResolution From(string value, SecretOrigin origin) => new(value, origin);

    public static SecretResolution Unresolved(string problem) =>
        new(null, SecretOrigin.Unresolved, problem);

    /// <summary>True when the stored form was a pointer rather than the secret itself.</summary>
    public bool IsReference =>
        Origin is SecretOrigin.Environment or SecretOrigin.File or SecretOrigin.Unresolved;

    /// <summary>True when a usable value came back.</summary>
    public bool HasValue => !string.IsNullOrEmpty(Value);
}

/// <summary>
/// Turns a stored credential into its actual value, so a secret can be kept out of the database entirely.
/// </summary>
/// <remarks>
/// <para>
/// A stored value is either the secret itself or a reference to one — see <see cref="SecretReference"/>
/// for the syntax. Operators who manage secrets properly store references and their database holds no
/// credential at all; operators who just want the UI to work keep typing keys and nothing changes for
/// them. Recorded in <c>docs/plans/2026-08-09 - secret-handling.md</c>.
/// </para>
/// <para>
/// <b>Resolution happens at the point of use, never on repository read.</b> Resolving on read would let an
/// edit round-trip write the resolved secret back as a literal, silently converting a properly-managed
/// reference into stored plaintext — worse than where we started.
/// </para>
/// </remarks>
public interface ISecretResolver
{
    /// <summary>Resolves a stored credential. Never throws; an unreadable reference comes back as
    /// <see cref="SecretOrigin.Unresolved"/> with a <see cref="SecretResolution.Problem"/>.</summary>
    SecretResolution Resolve(string? stored);
}

/// <summary>
/// The stored-credential reference syntax. Pure string handling, so the Application layer can tell a
/// reference from a literal (for masking and validation) without reaching for the resolver, which does IO.
/// </summary>
public static class SecretReference
{
    /// <summary>Read the value from this environment variable.</summary>
    public const string EnvironmentScheme = "env:";

    /// <summary>Read the value from this file — typically a Docker/Kubernetes mounted secret.</summary>
    public const string FileScheme = "file:";

    /// <summary>
    /// Force the remainder to be taken literally. For the operator whose real key genuinely begins with
    /// one of the other schemes — rare, but silently misreading a credential as a pointer is unkind.
    /// </summary>
    public const string LiteralScheme = "literal:";

    /// <summary>
    /// True when the stored value points at a secret rather than being one — which also makes it safe to
    /// display, since a pointer is not a credential.
    /// </summary>
    public static bool IsReference(string? stored) =>
        Matches(stored, EnvironmentScheme) || Matches(stored, FileScheme);

    /// <summary>True when the stored value carries the given scheme prefix.</summary>
    public static bool Matches(string? stored, string scheme) =>
        stored is not null
        && stored.AsSpan().TrimStart().StartsWith(scheme, StringComparison.OrdinalIgnoreCase);

    /// <summary>Strips a scheme prefix, returning the remainder trimmed.</summary>
    public static string Strip(string stored, string scheme) =>
        stored.Trim()[scheme.Length..].Trim();
}
