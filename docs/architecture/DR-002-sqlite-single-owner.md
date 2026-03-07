# DR-002 — SQLite Single Owner Pattern

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-03-08 |
| **Deciders** | Architect |

## Context

SQLite does not support concurrent writes from multiple processes. With a multi-container deployment, naive use of a shared SQLite file would cause write contention and corruption.

## Decision

The `core-worker` container is the **sole owner** of the SQLite database file. All other services (api, worker) communicate with the core-worker via Wolverine messages — they never access the database directly.

The SQLite file is persisted via a named Docker volume mounted only to the core-worker container.

## Consequences

- ✅ No SQLite write contention
- ✅ Clear data ownership boundary
- ✅ EF Core migrations run in a single controlled process
- ✅ Swap to PostgreSQL later: one-line EF Core provider change
- ⚠️ All data access requires message round-trips from api/worker to core-worker
