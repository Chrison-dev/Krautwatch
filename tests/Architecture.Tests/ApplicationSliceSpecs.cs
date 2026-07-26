using ArchUnitNET.Loader;
using ArchUnitNET.xUnit;
using Xunit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace Krautwatch.Architecture.Tests;

/// <summary>
/// Application is cut into vertical feature slices (DR-009). A slice is self-contained: it must not
/// depend on a sibling slice. Shared building blocks belong in Domain or a dedicated shared slice.
/// </summary>
public class ApplicationSliceSpecs
{
    private static readonly ArchUnitNET.Domain.Architecture Architecture = new ArchLoader()
        .LoadAssemblies(typeof(Krautwatch.Application.ApplicationServiceExtensions).Assembly)
        .Build();

    [Theory]
    [InlineData("Catalog", "Crawling|Downloads|Settings")]
    [InlineData("Crawling", "Catalog|Downloads|Settings")]
    [InlineData("Downloads", "Catalog|Crawling|Settings")]
    [InlineData("Settings", "Catalog|Crawling|Downloads")]
    public void Slice_does_not_depend_on_sibling_slices(string slice, string siblings)
    {
        Types().That().ResideInNamespaceMatching($@"Krautwatch\.Application\.{slice}")
            .Should().NotDependOnAnyTypesThat()
            .ResideInNamespaceMatching($@"Krautwatch\.Application\.({siblings})")
            .Because($"Application slices are vertical and independent; {slice} must not depend on sibling slices (DR-009).")
            .Check(Architecture);
    }
}
