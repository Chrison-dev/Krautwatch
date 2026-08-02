using System.Text.RegularExpressions;
using Fallout.Common;
using Fallout.Common.IO;
using Fallout.Common.Tooling;
using Serilog;

/// <summary>
/// GitHub Release publishing (#24).
/// </summary>
/// <remarks>
/// <para>
/// A release is a <b>build artifact</b>, not something clicked into the GitHub UI. What a user actually
/// needs in order to run Krautwatch is a compose file plus an environment template pointing at the
/// images that were just published — so that is what the release contains, produced by the same build
/// that produced the images.
/// </para>
/// <para>
/// It is a separate target behind its own environment for the same reason the registries are:
/// publishing to one destination should not imply publishing to another, and an environment is what
/// carries the approval.
/// </para>
/// </remarks>
partial class Build
{
    AbsolutePath ReleaseDirectory => RootDirectory / ".artifacts" / "release";

    /// <summary>
    /// The git tag being released, "v"-prefixed — the release's identity, as opposed to
    /// <see cref="EffectiveTag"/> which is the image tag without the "v".
    /// </summary>
    string ReleaseTag
    {
        get
        {
            var reference = Environment.GetEnvironmentVariable("GITHUB_REF_NAME");

            if (Environment.GetEnvironmentVariable("GITHUB_REF_TYPE") == "tag"
                && !string.IsNullOrWhiteSpace(reference))
            {
                return reference;
            }

            // Local runs: whatever tag points at HEAD. Deliberately exact-match — releasing "the nearest
            // tag" from an untagged commit would publish a release whose contents are not that tag.
            var probe = ProcessTasks.StartProcess("git", "describe --tags --exact-match", RootDirectory,
                logOutput: false);
            probe.WaitForExit();

            return probe.ExitCode == 0
                ? probe.Output.Select(x => x.Text).FirstOrDefault()?.Trim()
                : null;
        }
    }

    /// <summary>
    /// Builds the release artifact. Publishes nothing — so it is safe to run, and reviewable before it
    /// goes anywhere.
    /// </summary>
    Target ReleaseBundle => _ => _
        .Description("Build the deployable release bundle (compose + env template + notes)")
        .DependsOn(Compose)
        .Executes(() =>
        {
            AssertTagged();

            var assets = BuildReleaseBundle();
            File.WriteAllText(ReleaseDirectory / "notes.md", BuildReleaseNotes());

            Log.Information("Release bundle for {Tag}: {Assets}", ReleaseTag,
                string.Join(", ", assets.Select(x => x.Name)));
        });

    Target GitHubRelease => _ => _
        .Description("Publish the GitHub Release for the current tag")
        .DependsOn(ReleaseBundle)
        .Executes(() =>
        {
            AssertTagged();
            AssertPublishedImagesExist();

            var assets = new[] { ReleaseDirectory / "docker-compose.yaml", ReleaseDirectory / ".env.example" };

            // No --generate-notes: the notes file already contains GitHub's generated changelog, with
            // the install section after it. The flag would append a second copy at the bottom.
            ProcessTasks.StartProcess("gh",
                    $"release create {ReleaseTag} " +
                    $"--title {ReleaseTag} " +
                    $"--notes-file {ReleaseDirectory / "notes.md"} " +
                    string.Join(" ", assets.Select(x => $"\"{x}\"")),
                    RootDirectory)
                .AssertZeroExitCode();

            Log.Information("Published release {Tag} with {Count} asset(s)", ReleaseTag, assets.Length);
        });

    /// <summary>
    /// Fails unless HEAD is exactly a tag.
    /// </summary>
    /// <remarks>
    /// Not <c>.Requires()</c>: that only inspects injected parameters, and a computed property fails it
    /// with "not marked with an injection attribute" rather than with anything about tags.
    /// </remarks>
    void AssertTagged() =>
        Assert.NotNullOrEmpty(ReleaseTag,
            "No tag points at HEAD — a release must be cut from a tagged commit.");

    /// <summary>
    /// Fails unless every image the bundle references is actually on the registry.
    /// </summary>
    /// <remarks>
    /// The tag push starts the image publish and this workflow at the same moment, so without a check
    /// the release can win the race and ship a compose file pointing at images that do not exist yet —
    /// or, if the image publish failed outright, that never will. Waiting is the honest behaviour: a
    /// release is a promise that the thing is installable.
    /// </remarks>
    void AssertPublishedImagesExist()
    {
        var deadline = TimeSpan.FromMinutes(20);
        var interval = TimeSpan.FromSeconds(30);
        var waited = TimeSpan.Zero;

        foreach (var service in Services.Select(s => s.Service))
        {
            var image = $"{RemoteImage(service)}:{EffectiveTag}";

            while (true)
            {
                var probe = ProcessTasks.StartProcess("docker", $"buildx imagetools inspect {image}",
                    RootDirectory, logOutput: false);
                probe.WaitForExit();

                if (probe.ExitCode == 0)
                {
                    Log.Information("Found {Image}", image);
                    break;
                }

                if (waited >= deadline)
                    Assert.Fail($"{image} is not on the registry after {deadline.TotalMinutes:0} minutes.");

                Log.Information("Waiting for {Image} to appear on the registry…", image);
                Thread.Sleep(interval);
                waited += interval;
            }
        }
    }

    /// <summary>
    /// Turns the generated compose output into something a stranger can actually deploy.
    /// </summary>
    /// <remarks>
    /// Two changes matter. The generated <c>.env</c> names images unqualified and at the AppHost's own
    /// version, which resolves to nothing on a machine that did not build them — so image references are
    /// rewritten to the published registry coordinates. And the generated file contains <b>real
    /// generated secrets</b> (the Postgres password and the instance API key); attaching that verbatim
    /// to a public release would publish working credentials, so every secret is blanked.
    /// </remarks>
    List<AbsolutePath> BuildReleaseBundle()
    {
        ReleaseDirectory.CreateOrCleanDirectory();

        var compose = ReleaseDirectory / "docker-compose.yaml";
        File.Copy(ComposeDirectory / "docker-compose.yaml", compose, overwrite: true);

        // Anything that is a credential rather than a setting. Blanked rather than dropped, so the file
        // still documents what has to be filled in.
        string[] secrets = ["POSTGRES_PASSWORD", "KRAUTWATCH_APIKEY", "TVDB_APIKEY"];

        var lines = File.ReadAllLines(ComposeDirectory / ".env")
            .Select(line =>
            {
                var match = Regex.Match(line, @"^(?<key>[A-Z0-9_]+)=(?<value>.*)$");
                if (!match.Success)
                    return line;

                var key = match.Groups["key"].Value;

                if (secrets.Contains(key))
                    return $"{key}=";

                if (key.EndsWith("_IMAGE", StringComparison.Ordinal))
                {
                    var service = ServiceForEnvKey(key);
                    if (service is not null)
                        return $"{key}={RemoteImage(service)}:{EffectiveTag}";
                }

                return line;
            })
            .ToList();

        var env = ReleaseDirectory / ".env.example";
        File.WriteAllLines(env, lines);

        Log.Information("Release bundle written to {Directory}", ReleaseDirectory);

        return [compose, env];
    }

    /// <summary>Maps a generated <c>*_IMAGE</c> env key back to the service it names.</summary>
    /// <remarks>
    /// The generator derives the key from the compose service name by upper-casing and replacing "-"
    /// with "_", so this reverses that rather than duplicating the list of services.
    /// </remarks>
    static string ServiceForEnvKey(string key) =>
        Services.Select(s => s.Service).FirstOrDefault(service =>
            $"{service.ToUpperInvariant().Replace('-', '_')}_IMAGE" == key);

    /// <summary>
    /// Asks GitHub to generate the changelog from merged PRs, then appends the install section.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The narrative comes from the PRs, grouped by label per <c>.github/release.yml</c> — so the notes
    /// say what the reviewed changes said, and improving them is a matter of labelling PRs rather than
    /// of writing release prose twice.
    /// </para>
    /// <para>
    /// Generated through the API rather than <c>gh release create --generate-notes</c> so the build
    /// controls the order: the changelog leads and the install section follows. The flag always appends
    /// its output last, which would bury the changelog under boilerplate.
    /// </para>
    /// </remarks>
    string BuildReleaseNotes()
    {
        // gh expands the {owner}/{repo} placeholders from the checkout's origin remote.
        var probe = ProcessTasks.StartProcess("gh",
            $"api repos/{{owner}}/{{repo}}/releases/generate-notes -f tag_name={ReleaseTag} --jq .body",
            RootDirectory, logOutput: false);
        probe.WaitForExit();
        probe.AssertZeroExitCode();

        var changelog = string.Join(Environment.NewLine, probe.Output.Select(x => x.Text)).Trim();

        var pull = string.Join(Environment.NewLine,
            Services.Select(s => $"docker pull {RemoteImage(s.Service)}:{EffectiveTag}"));

        return $"""
                {changelog}

                ## Install

                Download `docker-compose.yaml` and `.env.example` below, rename the latter to `.env`, fill in
                `POSTGRES_PASSWORD` and `KRAUTWATCH_APIKEY` (and `TVDB_APIKEY` if you want TheTVDB matching),
                then:

                ```bash
                docker compose up -d
                ```

                Images for this release (linux/amd64 + linux/arm64):

                ```
                {pull}
                ```
                """;
    }
}
