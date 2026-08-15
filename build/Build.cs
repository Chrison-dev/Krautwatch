using Fallout.Common;
using Fallout.Common.IO;
using Fallout.Common.Tools.DotNet;
using static Fallout.Common.Tools.DotNet.DotNetTasks;

/// <summary>
/// Fallout build for Krautwatch — the targets. The CI/CD definition that invokes them lives in
/// <c>Build.CI.GitHubActions.cs</c>, from which every <c>.github/workflows/*.yml</c> is GENERATED;
/// never hand-edit those.
///
/// Live network tests (ARD/ZDF) are tagged [Trait("Category","Live")] and EXCLUDED from the
/// default Test run — external APIs drift/rate-limit. Run them on demand: ./build.cmd TestLive
/// </summary>
partial class Build : FalloutBuild
{
    public static int Main() => Execute<Build>(x => x.Test);

    AbsolutePath SolutionFile => RootDirectory / "Krautwatch.slnx";

    Target Compile => _ => _
        .Description("Build the solution")
        .Executes(() => DotNetBuild(_ => _
            .SetProjectFile(SolutionFile)
            .SetConfiguration("Release")));

    Target Test => _ => _
        .Description("Run the unit tests (excludes live network tests)")
        .DependsOn(Compile)
        .Executes(() => DotNetTest(_ => _
            .SetProjectFile(SolutionFile)
            .SetConfiguration("Release")
            .SetFilter("Category!=Live")
            .EnableNoBuild()));

    Target TestLive => _ => _
        .Description("Run ONLY the live network tests against the real ARD/ZDF APIs")
        .DependsOn(Compile)
        .Executes(() => DotNetTest(_ => _
            .SetProjectFile(SolutionFile)
            .SetConfiguration("Release")
            .SetFilter("Category=Live")
            .EnableNoBuild()));
}
