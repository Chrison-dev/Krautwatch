using Fallout.Common;
using Fallout.Common.CI.GitHubActions;
using Fallout.Common.IO;
using Fallout.Common.Tools.DotNet;
using static Fallout.Common.Tools.DotNet.DotNetTasks;

/// <summary>
/// Fallout build for Krautwatch. CI (build.yml — GENERATED from the [GitHubActions] attribute;
/// never hand-edit it) compiles the solution and runs the unit tests on push/PR to main.
///
/// Live network tests (ARD/ZDF) are tagged [Trait("Category","Live")] and EXCLUDED from the
/// default Test run — external APIs drift/rate-limit. Run them on demand: ./build.cmd TestLive
/// </summary>
[GitHubActions(
    "build",
    GitHubActionsImage.UbuntuLatest,
    FetchDepth = 0,
    OnPushBranches = new[] { "main" },
    OnPullRequestBranches = new[] { "main" },
    InvokedTargets = new[] { nameof(Test) })]

// ── Image publishing (#24) ────────────────────────────────────────────────────
//
// One workflow per registry, each bound to a GitHub *environment* of the same name. The environment
// is what holds that registry's credentials, so a token for one registry is never in scope for a
// push to another — and an environment can carry protection rules (required reviewers, tag filters)
// without any of that leaking into the build definition.
//
// Adding a registry is a third attribute plus an environment holding REGISTRY_USER and
// REGISTRY_PASSWORD; nothing in the targets changes.
// The one dispatch input both publish workflows take, declared once and scoped to them. Typed and
// compile-checked, replacing the untyped OnWorkflowDispatchOptionalInputs arrays this used to carry —
// misconfiguration now fails generation instead of emitting broken YAML (FALLOUTOBS001).
[GitHubActionsInput(
    "ImageTag",
    Type = GitHubActionsInputType.String,
    Description = "Image tag to publish. Defaults to the build's own tag resolution when left blank.",
    Workflows = new[] { "publish-ghcr", "publish-dockerhub" })]
[GitHubActions(
    "publish-ghcr",
    GitHubActionsImage.UbuntuLatest,
    FetchDepth = 0,
    OnPushTags = new[] { "v*" },
    // A dedicated target per registry rather than a Registry parameter: the generator does not emit
    // the attribute's Env into the workflow, and a target name states the destination unambiguously.
    InvokedTargets = new[] { nameof(PushGhcr) },
    EnvironmentName = "ghcr",
    ImportSecrets = new[] { nameof(RegistryUser), nameof(RegistryPassword) })]
// The GitHub Release is a publish destination like any other: same tag trigger, its own environment,
// its own approval. It builds nothing new — it packages the compose output and points at the images
// the sibling workflow publishes, refusing to go out until those images are actually on the registry.
[GitHubActions(
    "publish-release",
    GitHubActionsImage.UbuntuLatest,
    FetchDepth = 0,
    OnPushTags = new[] { "v*" },
    InvokedTargets = new[] { nameof(GitHubRelease) },
    EnvironmentName = "github-release",
    // Creating a release writes to the repository, which the default token is not granted.
    EnableGitHubToken = true,
    WritePermissions = new[] { GitHubActionsPermissions.Contents })]
[GitHubActions(
    "publish-dockerhub",
    GitHubActionsImage.UbuntuLatest,
    FetchDepth = 0,
    // Deliberately tag-triggered only via dispatch: Docker Hub is a mirror, and mirroring on every
    // tag doubles the blast radius of a bad release for no benefit.
    InvokedTargets = new[] { nameof(PushDockerHub) },
    EnvironmentName = "dockerhub",
    ImportSecrets = new[] { nameof(RegistryUser), nameof(RegistryPassword) })]
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
