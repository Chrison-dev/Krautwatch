using ArchUnitNET.Domain;
using ArchUnitNET.Loader;
using ArchUnitNET.xUnit;
using Xunit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace Krautwatch.Architecture.Tests;

/// <summary>
/// Enforces the DR-009 hexagon: Domain ← Application ← Infrastructure, with Presentation
/// as the outer composition layer. Dependencies may only point inward.
/// </summary>
public class HexagonalArchitectureSpecs
{
    private static readonly ArchUnitNET.Domain.Architecture Architecture = new ArchLoader()
        .LoadAssemblies(
            typeof(Krautwatch.Domain.Entities.Episode).Assembly,
            typeof(Krautwatch.Application.ApplicationServiceExtensions).Assembly,
            typeof(Krautwatch.Infrastructure.InfrastructureServiceExtensions).Assembly)
        .Build();

    [Fact]
    public void Domain_depends_on_no_other_krautwatch_layer()
    {
        Types().That().ResideInNamespaceMatching(@"Krautwatch\.Domain")
            .Should().NotDependOnAnyTypesThat()
            .ResideInNamespaceMatching(@"Krautwatch\.(Application|Infrastructure|Api|Web|ServiceDefaults|AppHost)")
            .Because("Domain is the core of the hexagon and must depend on nothing else (DR-009).")
            .Check(Architecture);
    }

    [Fact]
    public void Application_depends_only_on_domain()
    {
        Types().That().ResideInNamespaceMatching(@"Krautwatch\.Application")
            .Should().NotDependOnAnyTypesThat()
            .ResideInNamespaceMatching(@"Krautwatch\.(Infrastructure|Api|Web|ServiceDefaults|AppHost)")
            .Because("Application may only depend on Domain (DR-009).")
            .Check(Architecture);
    }

    [Fact]
    public void Infrastructure_does_not_depend_on_presentation()
    {
        Types().That().ResideInNamespaceMatching(@"Krautwatch\.Infrastructure")
            .Should().NotDependOnAnyTypesThat()
            .ResideInNamespaceMatching(@"Krautwatch\.(Api|Web|ServiceDefaults|AppHost)")
            .Because("Infrastructure implements ports; it must not reach into the Presentation hosts (DR-009).")
            .Check(Architecture);
    }
}
