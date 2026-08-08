using Krautwatch.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace Krautwatch.Infrastructure.Secrets;

/// <summary>
/// Resolves stored credentials that may be references — <c>env:NAME</c>, <c>file:/path</c> — so a secret
/// need never be written to the database. See <c>docs/plans/2026-08-09 - secret-handling.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// A reference resolves <b>in the process that uses it</b>. If a later feature reads `*arr` keys from a
/// different host than the one that tested them (#6's reach-back), that host needs the same variable or
/// mounted file. The self-hosting guide says so, because "it tests fine but reach-back 401s" is otherwise
/// unexplainable.
/// </para>
/// <para>
/// Nothing here logs a resolved value. Failures name the variable or path only.
/// </para>
/// </remarks>
public class SecretResolver(ILogger<SecretResolver> logger) : ISecretResolver
{
    public SecretResolution Resolve(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return SecretResolution.Empty();

        var text = stored.Trim();

        // Checked first: an operator who escapes a value wants it taken at face value, whatever it spells.
        if (SecretReference.Matches(text, SecretReference.LiteralScheme))
            return SecretResolution.Literal(SecretReference.Strip(text, SecretReference.LiteralScheme));

        if (SecretReference.Matches(text, SecretReference.EnvironmentScheme))
            return FromEnvironment(SecretReference.Strip(text, SecretReference.EnvironmentScheme));

        if (SecretReference.Matches(text, SecretReference.FileScheme))
            return FromFile(SecretReference.Strip(text, SecretReference.FileScheme));

        return SecretResolution.Literal(text);
    }

    private SecretResolution FromEnvironment(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return SecretResolution.Unresolved("A reference of 'env:' names no environment variable.");

        var value = Environment.GetEnvironmentVariable(name);

        if (string.IsNullOrEmpty(value))
        {
            logger.LogWarning(
                "Secret reference could not be resolved: environment variable {Variable} is not set in "
                + "this process.", name);

            return SecretResolution.Unresolved(
                $"Environment variable {name} is not set in this container.");
        }

        return SecretResolution.From(value.Trim(), SecretOrigin.Environment);
    }

    private SecretResolution FromFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return SecretResolution.Unresolved("A reference of 'file:' names no path.");

        try
        {
            if (!File.Exists(path))
            {
                logger.LogWarning(
                    "Secret reference could not be resolved: {Path} does not exist in this process.", path);

                return SecretResolution.Unresolved($"Secret file {path} does not exist in this container.");
            }

            // Secret files conventionally carry a trailing newline; `echo -n` is not something to require.
            var value = File.ReadAllText(path).Trim();

            if (value.Length == 0)
                return SecretResolution.Unresolved($"Secret file {path} is empty.");

            return SecretResolution.From(value, SecretOrigin.File);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Message, not the exception: a path is safe to log, but the stack of a file read on a secret
            // path is noise, and an unreadable secret is an operator problem rather than a defect.
            logger.LogWarning("Secret reference {Path} could not be read: {Reason}", path, ex.Message);
            return SecretResolution.Unresolved($"Secret file {path} could not be read: {ex.Message}");
        }
    }
}
