using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;
using Xunit;

namespace Krautwatch.Architecture.Tests;

/// <summary>
/// Replaces <c>TngTech.ArchUnitNET.xUnit</c>'s <c>Check()</c> extension. That package still depends on
/// <c>xunit.assert</c> 2.x, which cannot coexist with xunit v3, so we evaluate the rule against the
/// core ArchUnitNET API and fail through xunit ourselves.
/// </summary>
internal static class ArchRuleAssert
{
    /// <summary>Evaluates the rule and fails the test with every violation listed if it does not hold.</summary>
    public static void Check(this IArchRule rule, ArchUnitNET.Domain.Architecture architecture)
    {
        if (rule.HasNoViolations(architecture)) return;

        var violations = rule.Evaluate(architecture)
            .Where(result => !result.Passed)
            .Select(result => $"  - {result.Description}")
            .ToList();

        Assert.Fail($"Architecture rule violated: {rule.Description}{Environment.NewLine}"
                    + string.Join(Environment.NewLine, violations));
    }
}
