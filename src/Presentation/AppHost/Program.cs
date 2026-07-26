var builder = DistributedApplication.CreateBuilder(args);

// ──────────────────────────────────────────────────────────────
// Postgres (DR-009) — Aspire provisions the container + database and injects the
// connection string into referencing services as ConnectionStrings:krautwatch.
// ──────────────────────────────────────────────────────────────
var postgres = builder.AddPostgres("postgres");
var db = postgres.AddDatabase("krautwatch");

// Migrator (DR-009) — a run-to-completion resource that owns the EF schema. Every DB consumer
// WaitForCompletion(migrator), so migration ownership no longer lives inside an app host.
var migrator = builder.AddProject<Projects.Krautwatch_Migrator>("migrator")
    .WithReference(db)
    .WaitFor(db);

// Newznab + SABnzbd — the public *arr-facing surface (indexer + download client).
builder.AddProject<Projects.Krautwatch_Api_NewznabIndexerApi>("newznab")
    .WithReference(db)
    .WaitFor(db)
    .WaitForCompletion(migrator)
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints();

// ──────────────────────────────────────────────────────────────
// Agents (DR-009) — per-broadcaster crawlers + the downloader, each an independently
// deployable microservice sharing Postgres + the durable Wolverine message store.
// Behaviour is filled in by the Application/Crawling slices in a later increment.
// ──────────────────────────────────────────────────────────────
builder.AddProject<Projects.Krautwatch_Agents_Ard>("agent-ard")
    .WithReference(db).WaitFor(db).WaitForCompletion(migrator)
    .WithHttpHealthCheck("/health");

builder.AddProject<Projects.Krautwatch_Agents_Zdf>("agent-zdf")
    .WithReference(db).WaitFor(db).WaitForCompletion(migrator)
    .WithHttpHealthCheck("/health");

builder.AddProject<Projects.Krautwatch_Agents_Downloader>("agent-downloader")
    .WithReference(db).WaitFor(db).WaitForCompletion(migrator)
    .WithHttpHealthCheck("/health");

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
