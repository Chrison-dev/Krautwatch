var builder = DistributedApplication.CreateBuilder(args);

// ──────────────────────────────────────────────────────────────
// Postgres (DR-009) — Aspire provisions the container + database and injects the
// connection string into referencing services as ConnectionStrings:krautwatch.
// ──────────────────────────────────────────────────────────────
var postgres = builder.AddPostgres("postgres");
var db = postgres.AddDatabase("krautwatch");

// The *arr-facing API surface (Newznab + SABnzbd + RSS). Owns EF migrations.
var api = builder.AddProject<Projects.Krautwatch_Api>("api")
    .WithReference(db)
    .WaitFor(db)
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints();

// Blazor instance-config UI — talks to the API.
builder.AddProject<Projects.Krautwatch_Web>("web")
    .WithReference(api)
    .WaitFor(api)
    .WithExternalHttpEndpoints();

// ──────────────────────────────────────────────────────────────
// Agents (DR-009) — per-broadcaster crawlers + the downloader, each an independently
// deployable microservice sharing Postgres + the durable Wolverine message store.
// Behaviour is filled in by the Application/Crawling slices in a later increment.
// ──────────────────────────────────────────────────────────────
builder.AddProject<Projects.Krautwatch_Agents_Ard>("agent-ard")
    .WithReference(db).WaitFor(db)
    .WithHttpHealthCheck("/health");

builder.AddProject<Projects.Krautwatch_Agents_Zdf>("agent-zdf")
    .WithReference(db).WaitFor(db)
    .WithHttpHealthCheck("/health");

builder.AddProject<Projects.Krautwatch_Agents_Downloader>("agent-downloader")
    .WithReference(db).WaitFor(db)
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
