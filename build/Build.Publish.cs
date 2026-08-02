using Fallout.Common;
using Fallout.Common.IO;
using Fallout.Common.Tooling;
using Fallout.Common.Tools.Docker;
using Fallout.Common.Tools.DotNet;
using Serilog;
using static Fallout.Common.Tools.DotNet.DotNetTasks;
using static Fallout.Common.Tools.Docker.DockerTasks;

/// <summary>
/// Container image and docker-compose publishing (#24).
/// </summary>
/// <remarks>
/// <para>
/// The compose file is <b>generated from the Aspire AppHost</b>, not hand-written. It is the same model
/// the dev fleet runs, so the deployed topology cannot quietly drift from the one that is tested daily —
/// which is the whole point of DR-003. Editing <c>.artifacts/compose/docker-compose.yaml</c> by hand is
/// therefore pointless; change <c>AppHost/Program.cs</c> and regenerate.
/// </para>
/// <para>
/// Images come from .NET SDK container publishing, so there are no Dockerfiles to keep in sync with the
/// projects — except the Downloader, which needs ffmpeg in the image and so cannot use it.
/// </para>
/// </remarks>
partial class Build
{
    [Parameter("Container registry to publish to, e.g. ghcr.io or docker.io")]
    readonly string Registry = "ghcr.io";

    /// <summary>Set by the per-registry targets; falls back to the --registry parameter.</summary>
    string _targetRegistry;

    string EffectiveRegistry => _targetRegistry ?? Registry;

    [Parameter("Registry namespace — the owner or organisation the images live under")]
    readonly string RegistryNamespace = "chrison-dev";

    [Parameter("Registry username for the push")]
    readonly string RegistryUser;

    [Parameter("Registry password or token for the push")]
    [Secret]
    readonly string RegistryPassword;

    [Parameter("Image tag. Defaults to the git tag on a tag build, otherwise 'dev'.")]
    readonly string ImageTag;

    AbsolutePath ComposeDirectory => RootDirectory / ".artifacts" / "compose";

    /// <summary>
    /// The tag the images are published under.
    /// </summary>
    /// <remarks>
    /// A tag-triggered release carries no workflow input, so falling straight through to "dev" would
    /// publish every release as "dev" and silently overwrite the previous one. <c>GITHUB_REF_NAME</c> is
    /// the tag on a tag build, and the leading "v" is stripped so the image tag reads 1.2.0 rather than
    /// v1.2.0 — the usual convention, and what a compose file will name.
    /// </remarks>
    string EffectiveTag
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(ImageTag))
                return ImageTag;

            var reference = Environment.GetEnvironmentVariable("GITHUB_REF_NAME");
            var isTagBuild = Environment.GetEnvironmentVariable("GITHUB_REF_TYPE") == "tag";

            return isTagBuild && !string.IsNullOrWhiteSpace(reference)
                ? reference.TrimStart('v')
                : "dev";
        }
    }

    /// <summary>
    /// The services that ship as images. Names match the compose service names, which is what makes the
    /// generated <c>.env</c> line up with what we push.
    /// </summary>
    static readonly (string Service, string Project, string Assembly, bool Ffmpeg)[] Services =
    [
        ("migrator",          "src/Presentation/Migrator/Krautwatch.Migrator.csproj",
                              "Krautwatch.Migrator.dll", false),
        ("newznab",           "src/Presentation/Api/NewznabIndexerApi/Krautwatch.Api.NewznabIndexerApi.csproj",
                              "Krautwatch.Api.NewznabIndexerApi.dll", false),
        ("web",               "src/Presentation/Web/Krautwatch.Web.csproj",
                              "Krautwatch.Web.dll", false),
        ("agent-ard",         "src/Presentation/Agents/Ard/Krautwatch.Agents.Ard.csproj",
                              "Krautwatch.Agents.Ard.dll", false),
        ("agent-zdf",         "src/Presentation/Agents/Zdf/Krautwatch.Agents.Zdf.csproj",
                              "Krautwatch.Agents.Zdf.dll", false),
        // The only service that needs ffmpeg: it remuxes HLS streams with `-c copy`.
        ("agent-downloader",  "src/Presentation/Agents/Downloader/Krautwatch.Agents.Downloader.csproj",
                              "Krautwatch.Agents.Downloader.dll", true),
    ];

    /// <summary>
    /// The name an image is built under locally — registry-agnostic on purpose.
    /// </summary>
    /// <remarks>
    /// Baking the registry into the build would mean rebuilding the same bits per registry, and the
    /// artifact pushed to Docker Hub would not be the one tested and pushed to GHCR. Build once, tag per
    /// registry at push time.
    /// </remarks>
    static string LocalImage(string service) => $"krautwatch-{service}";

    string RemoteImage(string service) =>
        $"{EffectiveRegistry}/{RegistryNamespace}/{LocalImage(service)}";

    Target Compose => _ => _
        .Description("Generate docker-compose.yaml + .env from the Aspire AppHost")
        .Executes(() =>
        {
            ComposeDirectory.CreateOrCleanDirectory();

            // `aspire publish` drives the AppHost's publish pipeline. Invoked through the CLI rather than
            // by running the AppHost directly: the CLI is what resolves the publisher and its arguments,
            // and it is the documented entry point.
            ProcessTasks.StartProcess("aspire",
                    $"publish --project src/Presentation/AppHost --output-path {ComposeDirectory}",
                    RootDirectory)
                .AssertZeroExitCode();

            Log.Information("Compose written to {Directory}", ComposeDirectory);
        });

    Target Images => _ => _
        .Description("Build container images for every service")
        .DependsOn(Compile)
        .Executes(() =>
        {
            foreach (var (service, project, assembly, ffmpeg) in Services)
            {
                DockerBuild(_ => _
                    .SetPath(RootDirectory)
                    .SetFile(RootDirectory / "docker/service.Dockerfile")
                    // AddBuildArg, not SetBuildArg: Set replaces the whole collection, so chaining it
                    // left only the last argument. PROJECT then arrived empty and `dotnet publish ""`
                    // tried to build the entire repo, test projects included.
                    .AddBuildArg($"PROJECT={project}")
                    .AddBuildArg($"ASSEMBLY={assembly}")
                    .AddBuildArg($"INSTALL_FFMPEG={ffmpeg.ToString().ToLowerInvariant()}")
                    .SetTag($"{LocalImage(service)}:{EffectiveTag}"));

                Log.Information("Built {Image}:{Tag}", LocalImage(service), EffectiveTag);
            }
        });

    Target Push => _ => _
        .Description("Push the images to --registry (defaults to ghcr.io)")
        .DependsOn(Images)
        .Requires(() => RegistryUser)
        .Requires(() => RegistryPassword)
        .Executes(PushImages);

    Target PushGhcr => _ => _
        .Description("Push to GitHub Container Registry — CI target for the 'ghcr' environment")
        .DependsOn(Images)
        .Requires(() => RegistryUser)
        .Requires(() => RegistryPassword)
        .Executes(() =>
        {
            _targetRegistry = "ghcr.io";
            PushImages();
        });

    Target PushDockerHub => _ => _
        .Description("Push to Docker Hub — CI target for the 'dockerhub' environment")
        .DependsOn(Images)
        .Requires(() => RegistryUser)
        .Requires(() => RegistryPassword)
        .Executes(() =>
        {
            _targetRegistry = "docker.io";
            PushImages();
        });

    /// <summary>Tags the locally built images for the target registry and pushes them.</summary>
    void PushImages()
    {
        DockerLogin(_ => _
            .SetServer(EffectiveRegistry)
            .SetUsername(RegistryUser)
            .SetPassword(RegistryPassword));

        try
        {
            foreach (var service in Services.Select(s => s.Service))
            {
                var local = $"{LocalImage(service)}:{EffectiveTag}";
                var remote = $"{RemoteImage(service)}:{EffectiveTag}";

                DockerTag(_ => _.SetSourceImage(local).SetTargetImage(remote));
                DockerPush(_ => _.SetName(remote));

                Log.Information("Pushed {Image}", remote);
            }
        }
        finally
        {
            // Leaves no credentials in the runner's docker config, even if a push failed.
            DockerLogout(_ => _.SetServer(EffectiveRegistry));
        }
    }
}
