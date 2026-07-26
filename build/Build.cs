using Fallout.Common;
using Fallout.Common.CI.GitHubActions;
using Fallout.Common.IO;
using Fallout.Common.Tools.DotNet;
using static Fallout.Common.Tools.DotNet.DotNetTasks;

/// <summary>
/// Fallout build for Krautwatch. CI (build.yml — GENERATED from the [GitHubActions] attribute;
/// never hand-edit it) compiles the solution and runs the unit tests on push/PR to main.
/// Regenerate the workflow with `./build.cmd` or:
///   dotnet fallout --generate-configuration GitHubActions_build --host GitHubActions
/// </summary>
[GitHubActions(
    "build",
    GitHubActionsImage.UbuntuLatest,
    FetchDepth = 0,
    OnPushBranches = new[] { "main" },
    OnPullRequestBranches = new[] { "main" },
    InvokedTargets = new[] { nameof(Test) })]
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
        .Description("Run the unit tests")
        .DependsOn(Compile)
        .Executes(() => DotNetTest(_ => _
            .SetProjectFile(SolutionFile)
            .SetConfiguration("Release")
            .EnableNoBuild()));
}
