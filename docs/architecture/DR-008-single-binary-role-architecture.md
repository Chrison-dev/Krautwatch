# DR-008 — Single-Binary Role-Switchable Worker

**Date:** 2026-03-10  
**Status:** Accepted  
**Supersedes:** DR-004 (container topology)

---

## Context

The original topology had two separate projects and Docker images:
- `MediathekNext.CoreWorker` — DB owner, catalog refresh
- `MediathekNext.Worker` — download execution via Wolverine

This made scaling awkward and duplicated infrastructure. The user wants a single binary
that can run in any role, so the same Docker image can serve as a full standalone node
or be composed into a scaled-out cluster.

Wolverine is also removed — it was only used for the download queue, which is now handled
by a SQLite/PostgreSQL/MSSQL-compatible job polling pattern using EF Core.

---

## Decision

### Single project: `MediathekNext.Worker`

One csproj, one Docker image, three roles:

| Role | Responsibilities |
|---|---|
| `standalone` | Everything: EF migrations, catalog refresh, API, downloads |
| `core` | EF migrations, catalog refresh, API — no downloads |
| `worker` | Download polling and execution only — no API, no catalog |

### Role detection (priority order — first match wins)

1. CLI argument: `--role worker`
2. Environment variable: `MEDIATHEK_ROLE=worker`
3. Config key: `"Role": "worker"` in appsettings.json
4. Default: `standalone`

### Generic job claiming — optimistic concurrency

Workers claim jobs using EF Core's concurrency token (`[ConcurrencyCheck]`).
No raw SQL, no dialect-specific locking. Works on SQLite, PostgreSQL, SQL Server, MySQL.

**Claim sequence:**
1. `SELECT TOP 1` job WHERE `Status = Queued` ORDER BY `CreatedAt ASC`
2. Set `Status = Downloading`, `WorkerId = <this worker's ID>`
3. `SaveChanges()` — EF Core includes the concurrency token in the WHERE clause
4. If `DbUpdateConcurrencyException` → another worker claimed it first → retry with next job

### Database provider abstraction

`AddInfrastructure()` accepts a `DbProviderOptions` record:
- Provider: `sqlite` | `postgres` | `mssql`
- ConnectionString

Switching databases requires only a config change. No code changes.

---

## Consequences

- Wolverine dependency removed entirely
- Single Docker image simplifies CI/CD
- `docker compose up --scale worker=3` works out of the box
- DB swap is a one-line config change
- Autoscaling deferred to DR-009 (future)
