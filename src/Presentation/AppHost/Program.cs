var builder = DistributedApplication.CreateBuilder(args);

// ──────────────────────────────────────────────────────────────
// The *arr-facing API surface (Newznab + SABnzbd + RSS).
// ──────────────────────────────────────────────────────────────
var api = builder.AddProject<Projects.Krautwatch_Api>("api")
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints();

// Blazor instance-config UI — talks to the API.
builder.AddProject<Projects.Krautwatch_Web>("web")
    .WithReference(api)
    .WaitFor(api)
    .WithExternalHttpEndpoints();

// TODO (DR-009 fleet — later increment): provision Postgres and add the crawler
// agents (Ard/Zdf) + Downloader agent here, wired to Postgres + the message transport.

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
