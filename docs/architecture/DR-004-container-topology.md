# DR-004 — Container Topology

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-03-08 |
| **Deciders** | Architect |

## Context

The application must be self-hosted via Docker Compose with clear separation of concerns and the ability to scale download workers independently.

## Decision

Five container roles:

| Container | Image Base | Scale | Responsibility |
|---|---|---|---|
| `frontend` | dotnet aspnet alpine | 1 | Blazor Server UI |
| `api` | dotnet aspnet alpine | 1–N | Stateless Minimal API, publishes Wolverine jobs |
| `worker` | dotnet runtime alpine + ffmpeg | 1–N | Consumes download jobs, runs ffmpeg |
| `core-worker` | dotnet runtime alpine | 1 (always) | Owns SQLite, catalog refresh, settings |
| `proxy` | Traefik alpine | 1 | Reverse proxy, routes /api/* and /* |
| `prometheus` | prom/prometheus | 1 | Scrapes /metrics from all containers |
| `loki` | grafana/loki | 1 | Collects stdout JSON logs |
| `grafana` | grafana/grafana | 1 | Dashboards for metrics and logs |

All containers share a Docker network. Only `proxy` is exposed externally.

Volumes:
- `sqlite-data` — mounted to `core-worker` only
- `downloads` — mounted to `core-worker` and all `worker` instances

## Consequences

- ✅ Workers scale independently with `--scale worker=N`
- ✅ SQLite ownership is enforced at the infrastructure level
- ✅ ffmpeg is pre-installed in the worker image — no host dependency
- ✅ Full observability via Grafana without cloud dependencies
