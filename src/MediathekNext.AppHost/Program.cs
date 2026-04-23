var builder = DistributedApplication.CreateBuilder(args);

// ──────────────────────────────────────────────────────────────
// Standalone mode (default for local dev)
// Single process: DB owner + catalog refresh + API + downloads
// ──────────────────────────────────────────────────────────────
var standalone = builder.AddProject<Projects.MediathekNext_Worker>("standalone")
    .WithEnvironment("MEDIATHEK_ROLE", "standalone")
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints();

// Blazor frontend — talks to standalone's API
builder.AddProject<Projects.MediathekNext_Web>("frontend")
    .WithReference(standalone)
    .WaitFor(standalone)
    .WithExternalHttpEndpoints();

// ──────────────────────────────────────────────────────────────
// Scaled-out mode (opt-in via launch profile "distributed")
// Uncomment and comment out "standalone" above to use this.
// ──────────────────────────────────────────────────────────────
//
// var core = builder.AddProject<Projects.MediathekNext_Worker>("core")
//     .WithEnvironment("MEDIATHEK_ROLE", "core")
//     .WithHttpHealthCheck("/health")
//     .WithExternalHttpEndpoints();
//
// builder.AddProject<Projects.MediathekNext_Worker>("worker")
//     .WithEnvironment("MEDIATHEK_ROLE", "worker")
//     .WithReference(core)
//     .WaitFor(core);
//
// builder.AddProject<Projects.MediathekNext_Web>("frontend")
//     .WithReference(core)
//     .WaitFor(core)
//     .WithExternalHttpEndpoints();

// ──────────────────────────────────────────────────────────────
// Observability (opt-in via launch profile "observability")
// ──────────────────────────────────────────────────────────────
if (builder.Environment.EnvironmentName == "Observability")
{
    var prometheus = builder.AddContainer("prometheus", "prom/prometheus", "latest")
        .WithBindMount("../../docker/prometheus/prometheus.yml", "/etc/prometheus/prometheus.yml")
        .WithEndpoint(port: 9090, targetPort: 9090, name: "http");

    builder.AddContainer("grafana", "grafana/grafana", "latest")
        .WithBindMount("../../docker/grafana/provisioning", "/etc/grafana/provisioning")
        .WithEnvironment("GF_SECURITY_ADMIN_PASSWORD", "admin")
        .WithEndpoint(port: 3000, targetPort: 3000, name: "http");

    builder.AddContainer("loki", "grafana/loki", "latest")
        .WithEndpoint(port: 3100, targetPort: 3100, name: "http");
}

builder.Build().Run();
