using Fallout.Common.CI.GitHubActions;

/// <summary>
/// The CI/CD definition — every workflow this repository has, declared as an attribute.
/// </summary>
/// <remarks>
/// <para>
/// <b>`.github/workflows/*.yml` is GENERATED from what follows.</b> Hand-editing the YAML is
/// silently undone by the next generation. Change an attribute, then regenerate:
/// <c>dotnet fallout --generate-configuration GitHubActions_&lt;name&gt; --host GitHubActions</c>
/// (or just build the <c>_build</c> project, which regenerates all of them).
/// </para>
/// <para>
/// The governing principle is that <b>the build is defined in C#, not in YAML</b>. A workflow here
/// provisions a runner and routes a channel; every step that actually does something invokes a
/// Fallout target. That is why the same commands work identically on a laptop and on a runner —
/// and it is why there is no hand-written workflow in this repo, not even for publishing.
/// </para>
/// <para>
/// The branch model these triggers implement is GitFlow — see
/// <c>docs/branching-and-release.md</c>, with the per-workflow rationale in <c>docs/ci.md</c>.
/// </para>
/// </remarks>

// ── The gate ──────────────────────────────────────────────────────────────────
//
// Runs on every PR into a long-lived branch, and on every push to the two permanent ones. Feature
// branches build nothing until a PR is opened: there is no value in gating work that is not asking
// to land yet.
//
// Deliberately NO path exclusions. Excluding `**/*.md` would look like an easy saving, but the job
// name is the required status check — a docs-only PR would then wait forever on a check that never
// fires. The only fix is a second workflow reporting the same context on the exact inverse path
// set, which would have to be hand-written YAML. Not worth it for a five-minute build.
[GitHubActions(
    "build",
    GitHubActionsImage.UbuntuLatest,
    FetchDepth = 0,
    OnPushBranches = new[] { DevelopBranch, MainBranch },
    OnPullRequestBranches = new[]
    {
        DevelopBranch, MainBranch, ReleaseBranchPattern, HotfixBranchPattern, SupportBranchPattern,
    },
    InvokedTargets = new[] { nameof(Test) })]

// ── The edge channel (GitFlow's preview channel) ──────────────────────────────
//
// Every push to the trunk republishes the six images under `:edge`, so a tester can run the next
// release before it is a release. Mirrors the extension repo's rolling `preview` VSIX; the shape
// differs only because our artefact is a registry tag rather than a GitHub release asset.
//
// Path exclusions ARE safe here — this is not a required check, so a skipped run blocks nothing,
// and rebuilding six multi-arch images because a markdown file changed is pure waste.
//
// Concurrency QUEUES rather than cancels (ConcurrencyCancelInProgress is left at its default
// false): cancelling a push mid-way can leave `:edge` pointing at a half-written manifest list,
// which is worse than an edge build running a few minutes behind.
[GitHubActions(
    "publish-edge",
    GitHubActionsImage.UbuntuLatest,
    FetchDepth = 0,
    OnPushBranches = new[] { DevelopBranch },
    OnPushExcludePaths = new[] { "**/*.md", "docs/**" },
    ConcurrencyGroup = "${{ github.workflow }}",
    InvokedTargets = new[] { nameof(PushEdge) },
    EnvironmentName = "ghcr",
    ImportSecrets = new[] { nameof(RegistryUser), nameof(RegistryPassword) })]

// ── Image publishing (#24) ────────────────────────────────────────────────────
//
// One workflow per registry, each bound to a GitHub *environment* of the same name. The environment
// is what holds that registry's credentials, so a token for one registry is never in scope for a
// push to another — and an environment can carry protection rules (required reviewers, tag filters)
// without any of that leaking into the build definition.
//
// Adding a registry is a third attribute plus an environment holding REGISTRY_USER and
// REGISTRY_PASSWORD; nothing in the targets changes.
// The one dispatch input the release publish workflows take, declared once and scoped to them.
// Typed and compile-checked, replacing the untyped OnWorkflowDispatchOptionalInputs arrays this
// used to carry — misconfiguration now fails generation instead of emitting broken YAML
// (FALLOUTOBS001).
[GitHubActionsInput(
    "ImageTag",
    Type = GitHubActionsInputType.String,
    Description = "Image tag to publish. Defaults to the build's own tag resolution when left blank.",
    Workflows = new[] { "publish-ghcr", "publish-dockerhub" })]
[GitHubActions(
    "publish-ghcr",
    GitHubActionsImage.UbuntuLatest,
    FetchDepth = 0,
    OnPushTags = new[] { ReleaseTagPattern },
    // A dedicated target per registry rather than a Registry parameter: the generator does not emit
    // the attribute's Env into the workflow, and a target name states the destination unambiguously.
    InvokedTargets = new[] { nameof(PushGhcr) },
    EnvironmentName = "ghcr",
    ImportSecrets = new[] { nameof(RegistryUser), nameof(RegistryPassword) })]
// The GitHub Release is a publish destination like any other: same tag trigger, its own environment,
// its own approval. It builds nothing new — it packages the compose output and points at the images
// the sibling workflow publishes, refusing to go out until those images are actually on the registry.
//
// It also refuses to go out from the wrong branch: GitHubRelease asserts the tag is reachable from
// main or a support line (see Build.Release.cs). Under GitFlow the trunk is never tagged for
// release — it ships through the edge channel instead.
[GitHubActions(
    "publish-release",
    GitHubActionsImage.UbuntuLatest,
    FetchDepth = 0,
    OnPushTags = new[] { ReleaseTagPattern },
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
partial class Build
{
    /// <summary>Integration trunk and default branch. All finished work lands here first.</summary>
    const string DevelopBranch = "develop";

    /// <summary>Production. Only release and hotfix merges land here, and each one is tagged.</summary>
    const string MainBranch = "main";

    /// <summary>Short-lived stabilisation window cut from <see cref="DevelopBranch"/>.</summary>
    const string ReleaseBranchPattern = "release/*";

    /// <summary>Short-lived urgent production fix cut from <see cref="MainBranch"/>.</summary>
    const string HotfixBranchPattern = "hotfix/*";

    /// <summary>
    /// Long-lived maintenance line for a release <see cref="MainBranch"/> has moved past, e.g.
    /// <c>support/v0.3</c>. None exist today — wired up anyway, because the cost is one string and
    /// the alternative is discovering it is missing at the moment a user needs an old line patched.
    /// </summary>
    const string SupportBranchPattern = "support/*";

    /// <summary>
    /// The tag shape that triggers a release. Every publish channel keys on this, so an accidental
    /// tag is an accidental release — which is why <c>v*</c> is also covered by a tag ruleset.
    /// </summary>
    const string ReleaseTagPattern = "v*";
}
