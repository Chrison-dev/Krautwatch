var builder = DistributedApplication.CreateBuilder(args);

// ──────────────────────────────────────────────────────────────
// Deployment target (DR-003) — `aspire publish` renders this same model as a
// docker-compose.yaml, so the deployed topology cannot drift from the dev fleet.
// Inert during `dotnet run`; it only participates in publish.
// ──────────────────────────────────────────────────────────────
// Named "compose" rather than "krautwatch": resource names share one case-insensitive
// namespace, and the database is already called krautwatch.
var compose = builder.AddDockerComposeEnvironment("compose")
    .WithDashboard(dashboard => dashboard.WithHostPort(18888));

// ──────────────────────────────────────────────────────────────
// Postgres (DR-009) — Aspire provisions the container + database and injects the
// connection string into referencing services as ConnectionStrings:krautwatch.
// ──────────────────────────────────────────────────────────────
var postgres = builder.AddPostgres("postgres")
    // Without this the database lives in the container layer and a `compose down` silently discards
    // the catalog, every mapping and the admin account.
    .WithDataVolume("krautwatch-pgdata")
    .PublishAsDockerComposeService((_, service) =>
    {
        // The stock postgres image only creates the database named here. Aspire's AddDatabase is a
        // logical declaration that DCP materialises in dev; in compose nothing creates it, so the first
        // run came up with a healthy server and no "krautwatch" database at all.
        service.Environment["POSTGRES_DB"] = "krautwatch";

        // Compose's depends_on only waits for the *container*, not for Postgres to accept connections,
        // so without a healthcheck the migrator races it and dies with "Connection refused" — taking the
        // whole stack down, since everything gates on the migrator completing. The migrator also retries
        // on its own; this just means it rarely has to.
        service.Healthcheck = new()
        {
            Test = ["CMD-SHELL", "pg_isready -U postgres"],
            Interval = "5s",
            Timeout = "3s",
            Retries = 12,
            StartPeriod = "5s",
        };
    });

var db = postgres.AddDatabase("krautwatch");

// Migrator (DR-009) — a run-to-completion resource that owns the EF schema. Every DB consumer
// WaitForCompletion(migrator), so migration ownership no longer lives inside an app host.
var migrator = builder.AddProject<Projects.Krautwatch_Migrator>("migrator")
    .WithReference(db)
    .WaitFor(db);

// The one credential the *arr apps authenticate with. Generated if absent, so a fresh compose stack
// comes up secured rather than open — and Sonarr will not configure a SABnzbd download client at all
// without an API key, so leaving it blank is not a working configuration.
var apiKey = builder.AddParameter("krautwatch-apikey", secret: true);

// Optional: TheTVDB matching. Absent is fine — matching degrades to titles rather than failing.
var tvdbApiKey = builder.AddParameter("tvdb-apikey", secret: true);

// Newznab + SABnzbd — the public *arr-facing surface (indexer + download client).
builder.AddProject<Projects.Krautwatch_Api_NewznabIndexerApi>("newznab")
    .WithHttpEndpoint(port: 5055, targetPort: 8080, name: "http")
    .WithReference(db)
    .WaitFor(db)
    .WaitForCompletion(migrator)
    .WithEnvironment("Krautwatch__ApiKey", apiKey)
    .WithEnvironment("TvdbConfiguration__ApiKey", tvdbApiKey)
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints();

// Standalone UI — search / download / monitor without a Sonarr/Radarr instance (Blazor Server).
builder.AddProject<Projects.Krautwatch_Web>("web")
    .WithHttpEndpoint(port: 5099, targetPort: 8080, name: "http")
    .WithReference(db)
    .WaitFor(db)
    .WaitForCompletion(migrator)
    .WithEnvironment("TvdbConfiguration__ApiKey", tvdbApiKey)
    .WithExternalHttpEndpoints();

// ──────────────────────────────────────────────────────────────
// Agents (DR-009) — per-broadcaster crawlers + the downloader, each an independently
// deployable microservice sharing Postgres + the durable Wolverine message store.
// Behaviour is filled in by the Application/Crawling slices in a later increment.
// ──────────────────────────────────────────────────────────────
builder.AddProject<Projects.Krautwatch_Agents_Ard>("agent-ard")
    .WithHttpEndpoint(targetPort: 8080, name: "http")
    .WithReference(db).WaitFor(db).WaitForCompletion(migrator)
    .WithHttpHealthCheck("/health");

builder.AddProject<Projects.Krautwatch_Agents_Zdf>("agent-zdf")
    .WithHttpEndpoint(targetPort: 8080, name: "http")
    .WithReference(db).WaitFor(db).WaitForCompletion(migrator)
    .WithHttpHealthCheck("/health");

// Dev fleet runs as bare processes, so "/downloads" (a container mount in prod) is read-only here —
// point downloads at a writable temp dir. In compose it is the bind mount below.
var devDownloadDir = Path.Combine(Path.GetTempPath(), "krautwatch-downloads");

builder.AddProject<Projects.Krautwatch_Agents_Downloader>("agent-downloader")
    .WithHttpEndpoint(targetPort: 8080, name: "http")
    .WithEnvironment("Download__Directory", devDownloadDir)
    .WithReference(db).WaitFor(db).WaitForCompletion(migrator)
    .WithHttpHealthCheck("/health")
    .PublishAsDockerComposeService((_, service) =>
    {
        // Only the Downloader gets the media mount — nothing else writes files, and a service that
        // cannot reach the library cannot corrupt it.
        service.Volumes.Add(new()
        {
            Name = "downloads",
            Type = "bind",
            Source = "${KRAUTWATCH_DOWNLOADS:-./downloads}",
            Target = "/downloads",
        });

        // Overrides the dev temp path. Mount the same host directory into Sonarr at /downloads and the
        // two agree on paths with no remote-path mapping.
        service.Environment["Download__Directory"] = "/downloads";

        // ffmpeg is a hard dependency of the HLS remux path; the image bundles it, but say so loudly
        // if someone swaps the base image.
        service.Environment["Download__RequireFfmpeg"] = "true";
    });

// ──────────────────────────────────────────────────────────────
// Observability (opt-in via launch profile "observability")
// ──────────────────────────────────────────────────────────────
if (builder.Environment.EnvironmentName == "Observability")
{
    builder.AddContainer("prometheus", "prom/prometheus", "latest")
        .WithBindMount("../../../docker/prometheus/prometheus.yml", "/etc/prometheus/prometheus.yml")
        .WithEndpoint(port: 9090, targetPort: 9090, name: "http");

    builder.AddContainer("grafana", "grafana/grafana", "latest")
        .WithBindMount("../../../docker/grafana/provisioning", "/etc/grafana/provisioning")
        .WithEnvironment("GF_SECURITY_ADMIN_PASSWORD", "admin")
        .WithEndpoint(port: 3000, targetPort: 3000, name: "http");

    builder.AddContainer("loki", "grafana/loki", "latest")
        .WithEndpoint(port: 3100, targetPort: 3100, name: "http");
}

builder.Build().Run();
