# DR-009 — Architecture Reset: Hexagonal + Vertical-Slice CQRS/A Microservices

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-07-25 |
| **Deciders** | Christian |
| **Supersedes** | DR-002 (SQLite single-owner), DR-004 (container topology), DR-008 (single-binary role) |
| **Refines** | DR-005 (Wolverine messaging) |
| **Reaffirms** | DR-001 (provider ports), DR-003 (Aspire), DR-006 (observability), DR-007 (net10) |

## Context

The codebase accumulated contradictory decisions and half-migrations: messaging flip-flopped
Wolverine → EF-polling (DR-008) → TickerQ (both packages still referenced); topology flip-flopped
multi-container (DR-004) → single-binary roles (DR-008) with **both `Worker` and `CoreWorker`
projects still present** and `Worker → Api` referenced backwards. In parallel the product direction
shifted from a standalone Mediathek browser to a **Sonarr/Radarr indexer** (see DR-010).

Rather than keep patching, reset the structure to a deliberate architecture that matches how we
want to build and reason about it.

## Decision

### 1. Hexagonal / clean architecture — four layers

```
Domain  ←  Application  ←  Infrastructure
                  ↑              ↑
              Presentation (hosts + Aspire)
```

- **Domain** — entities, value objects, enums, and **ports** (interfaces). Zero dependencies.
- **Application** — use-cases as **vertical feature slices** (see §2). Depends only on Domain.
- **Infrastructure** — adapters implementing the ports: EF Core + Postgres persistence, broadcaster
  HTTP clients, ffmpeg, the messaging transport. Depends on Application + Domain.
- **Presentation** — all runnable hosts + the Aspire orchestrator. Composition root.

### 2. Folder convention — no namespace in folder names

Folders drop the assembly prefix; assemblies/namespaces stay fully qualified.

- `src/Domain/Krautwatch.Domain.csproj` → namespace `Krautwatch.Domain`
- `src/Application/…`, `src/Infrastructure/…`, `src/Presentation/…`

### 3. Application = vertical slices, CQRS/A **inside** each slice

The Application layer is cut **by feature ("application"), not by operation type**. Each slice is
cohesive, tested in isolation, and **promotable to its own project** later.

```
Application/
├── Crawling/
│   ├── Action/    ArdCrawling.cs, ZdfCrawling.cs   ← IO-driven orchestration
│   ├── Command/   (persist crawl results)
│   └── Query/
├── Downloads/     Action/ Command/ Query/
├── Indexing/      Query/ …   (Newznab search / RSS read models)
├── Settings/      Command/ Query/   (Sonarr/Radarr instances)
└── Abstractions/  ports the adapters implement
```

**CQRS + A (Actions):**

| | Touches | Runs on |
|---|---|---|
| **Command** | our own state (write) | Api, Agents |
| **Query** | our own state (read) | Api |
| **Action** | the outside world — **IO-driven** external orchestration; emits Commands/events | **Agents** |

### 4. Persistence — Postgres + EF Core

- **Postgres** is the store (Aspire provisions it). EF Core adapters live in
  `Infrastructure/Persistence`. The provider is abstracted (`AddInfrastructure(DbProviderOptions)`);
  postgres is default, sqlite/mssql remain swappable by config. **This removes the SQLite
  single-owner constraint (DR-002): every service talks to Postgres.**

### 5. Messaging — Wolverine, Postgres transport default

- **Wolverine** is the mediator + message bus + transactional outbox.
- **Transport is an Infrastructure concern.** Default transport is **Postgres-backed** (durable,
  no extra container — the one-command self-host stays app + Postgres). **RabbitMQ is an opt-in
  config swap** for people who scale agents out or already run a broker.
- Application only sees message contracts + a dispatch port; Infrastructure wires the transport.

### 6. Presentation — Aspire single entry, microservices from day one

```
Presentation/
├── AppHost/          .NET Aspire — single dev entry orchestrating the fleet
├── ServiceDefaults/  OTel / health, shared by all hosts
├── Api/              Newznab + SABnzbd + RSS (the *arr-facing surface)  — Queries + Commands
├── Web/              Blazor instance-config UI
└── Agents/           (was "Worker" — renamed to Agent)                  — Actions
    ├── Ard/          ARD (+ KiKA) crawler agent
    ├── Zdf/          ZDF crawler agent
    └── Downloader/   ffmpeg download execution
```

- **Aspire AppHost is the single entry point**; everything else is an independently deployable
  microservice from the start. Supersedes DR-004 (hand-rolled topology) and DR-008 (single binary).
- **Each broadcaster is its own crawler agent.** Adding a broadcaster = a new Application slice +
  Infrastructure client + Agent host.

### 7. Enforcement

The architecture is **baked into `CLAUDE.md`** and **enforced by architecture tests** (NetArchTest):
layer dependency rules, "Domain has zero project deps", "Application depends only on Domain",
"Presentation is not referenced by anything", and slice-isolation rules.

## Consequences

- ✅ One coherent model; the Wolverine/TickerQ/two-worker contradictions are gone.
- ✅ Vertical slices are cohesive and independently testable/extractable.
- ✅ Microservices + Aspire from day one; scale agents out without re-architecting.
- ✅ Self-host stays simple (app + Postgres) yet upgradeable (RabbitMQ) by config.
- ⚠️ Large one-time reset: rename MediathekNext → Krautwatch, move crawler logic into slices,
  delete `CoreWorker`, dissolve the `Crawlers.*` projects.
- ⚠️ Postgres is now a hard dependency (was optional SQLite) — acceptable for a self-hosted service.
