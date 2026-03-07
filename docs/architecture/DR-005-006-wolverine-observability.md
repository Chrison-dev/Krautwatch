# DR-005 — Wolverine for Messaging

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-03-08 |
| **Deciders** | Architect |

## Context

The application needs a message-passing mechanism between the API, workers, and core-worker. MediatR was the initial consideration but was replaced.

## Decision

Use **Wolverine** as the messaging and mediator framework.

- In-process transport for MVP (no broker required)
- Wolverine's middleware pipeline replaces MediatR behaviors
- Can upgrade to a durable transport (Wolverine over PostgreSQL, or RabbitMQ) with config changes only
- First-class .NET Worker Service integration

**FluentValidation** is used for message/command validation via Wolverine's middleware pipeline. License confirmed as Apache 2.0.

## Consequences

- ✅ No external broker dependency for MVP
- ✅ Clean upgrade path to durable messaging
- ✅ Replaces both MediatR and separate Worker Service scheduling

---

# DR-006 — Observability Stack

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-03-08 |
| **Deciders** | Architect |

## Context

The application runs as multiple containers. Debugging and monitoring requires centralised logs and metrics without cloud dependencies.

## Decision

**Metrics:** All .NET containers expose `/metrics` via `prometheus-net`. Prometheus scrapes all containers on a configured interval. Grafana queries Prometheus for dashboards.

**Logs:** All containers log structured JSON to stdout via `ILogger<T>`. Loki collects container logs via Docker log driver or Promtail sidecar. Grafana queries Loki for log exploration.

**Dev:** .NET Aspire dashboard provides traces, logs, and metrics during local development — Grafana is production-only.

## Consequences

- ✅ Zero cloud dependency
- ✅ Unified metrics and logs in one Grafana instance
- ✅ `ILogger<T>` is the only logging abstraction — no Serilog dependency
- ✅ Aspire dashboard covers dev needs without running the full observability stack locally
