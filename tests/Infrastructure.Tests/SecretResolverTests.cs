using Krautwatch.Domain.Interfaces;
using Krautwatch.Infrastructure.Secrets;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Krautwatch.Infrastructure.Tests;

/// <summary>
/// Covers the stored-credential reference syntax from
/// <c>docs/plans/2026-08-09 - secret-handling.md</c>.
/// </summary>
public class SecretResolverTests : IDisposable
{
    private readonly SecretResolver _resolver = new(NullLogger<SecretResolver>.Instance);
    private readonly List<string> _variables = [];
    private readonly List<string> _files = [];

    private void SetVariable(string name, string? value)
    {
        _variables.Add(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    private string WriteFile(string contents)
    {
        var path = Path.Combine(Path.GetTempPath(), $"krautwatch-secret-{Guid.NewGuid():N}");
        File.WriteAllText(path, contents);
        _files.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var name in _variables) Environment.SetEnvironmentVariable(name, null);
        foreach (var path in _files) File.Delete(path);
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_stored_resolves_to_empty(string? stored)
    {
        var result = _resolver.Resolve(stored);

        result.Origin.ShouldBe(SecretOrigin.Empty);
        result.HasValue.ShouldBeFalse();
    }

    [Fact]
    public void A_plain_value_is_the_secret_itself()
    {
        // The pre-existing behaviour, which must keep working untouched — every existing row is one of
        // these, which is why this change needs no data migration.
        var result = _resolver.Resolve("abc123def456");

        result.Value.ShouldBe("abc123def456");
        result.Origin.ShouldBe(SecretOrigin.Literal);
        result.IsReference.ShouldBeFalse();
    }

    [Fact]
    public void An_env_reference_reads_the_variable()
    {
        SetVariable("KRAUTWATCH_TEST_SONARR_KEY", "from-the-environment");

        var result = _resolver.Resolve("env:KRAUTWATCH_TEST_SONARR_KEY");

        result.Value.ShouldBe("from-the-environment");
        result.Origin.ShouldBe(SecretOrigin.Environment);
        result.IsReference.ShouldBeTrue();
    }

    [Fact]
    public void An_unset_env_reference_names_the_variable_rather_than_failing_silently()
    {
        // Returning an empty value here would authenticate with "" and report a 401 the operator cannot
        // explain — the exact confusion this failure mode exists to prevent.
        var result = _resolver.Resolve("env:KRAUTWATCH_TEST_DEFINITELY_NOT_SET");

        result.Origin.ShouldBe(SecretOrigin.Unresolved);
        result.HasValue.ShouldBeFalse();
        result.Problem.ShouldNotBeNull().ShouldContain("KRAUTWATCH_TEST_DEFINITELY_NOT_SET");
    }

    [Fact]
    public void A_file_reference_reads_the_file_and_tolerates_a_trailing_newline()
    {
        // Mounted secrets conventionally end with a newline; requiring `echo -n` would be a trap.
        var path = WriteFile("from-the-file\n");

        var result = _resolver.Resolve($"file:{path}");

        result.Value.ShouldBe("from-the-file");
        result.Origin.ShouldBe(SecretOrigin.File);
    }

    [Fact]
    public void A_missing_file_reference_names_the_path()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"krautwatch-absent-{Guid.NewGuid():N}");

        var result = _resolver.Resolve($"file:{missing}");

        result.Origin.ShouldBe(SecretOrigin.Unresolved);
        result.Problem.ShouldNotBeNull().ShouldContain(missing);
    }

    [Fact]
    public void An_empty_secret_file_is_a_problem_not_an_empty_key()
    {
        var path = WriteFile("   \n");

        var result = _resolver.Resolve($"file:{path}");

        result.Origin.ShouldBe(SecretOrigin.Unresolved);
    }

    [Fact]
    public void The_literal_scheme_escapes_a_key_that_looks_like_a_reference()
    {
        // Rare, but silently reading someone's real credential as a pointer would be unkind.
        var result = _resolver.Resolve("literal:env:not-actually-a-reference");

        result.Value.ShouldBe("env:not-actually-a-reference");
        result.Origin.ShouldBe(SecretOrigin.Literal);
    }

    [Fact]
    public void Schemes_are_case_insensitive_and_tolerate_surrounding_space()
    {
        SetVariable("KRAUTWATCH_TEST_CASE_KEY", "value");

        _resolver.Resolve("  ENV:KRAUTWATCH_TEST_CASE_KEY  ").Value.ShouldBe("value");
    }

    [Fact]
    public void A_scheme_with_no_target_is_a_problem_rather_than_a_literal()
    {
        _resolver.Resolve("env:").Origin.ShouldBe(SecretOrigin.Unresolved);
        _resolver.Resolve("file:").Origin.ShouldBe(SecretOrigin.Unresolved);
    }

    [Fact]
    public void A_reference_is_recognised_without_touching_the_environment()
    {
        // The Application layer masks and validates on syntax alone, so this must not need IO.
        SecretReference.IsReference("env:ANYTHING").ShouldBeTrue();
        SecretReference.IsReference("file:/run/secrets/x").ShouldBeTrue();
        SecretReference.IsReference("abc123").ShouldBeFalse();
        SecretReference.IsReference("literal:env:abc").ShouldBeFalse();
        SecretReference.IsReference(null).ShouldBeFalse();
    }
}
