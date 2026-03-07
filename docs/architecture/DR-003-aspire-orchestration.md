# DR-003 — .NET Aspire Orchestration

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-03-08 |
| **Deciders** | Architect |

## Context

The solution consists of multiple projects (frontend, api, two workers) plus infrastructure containers (Traefik, Prometheus, Loki, Grafana). Coordinating these during development with plain Docker Compose is friction-heavy and slows the inner loop.

## Decision

Use **.NET Aspire** as the local development orchestrator via an `AppHost` project. Aspire handles service discovery, connection strings, environment wiring, and provides a built-in dashboard.

For production, use `dotnet aspire publish` or the `Aspire.Hosting.Docker` integration to generate a `docker-compose.yml`. This ensures dev/prod parity without maintaining YAML manually.

## Consequences

- ✅ Single `dotnet run` in AppHost starts the entire stack
- ✅ Built-in Aspire dashboard for dev observability
- ✅ No Azure dependencies — Aspire is cloud-agnostic
- ✅ Docker Compose generated from the same source of truth
- ⚠️ Requires `dotnet workload install aspire` on developer machines
