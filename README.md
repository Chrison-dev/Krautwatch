# MediathekNext

Self-hosted web app to browse and download content from German public TV (ARD, ZDF).
Built on .NET 10, Blazor Server, SQLite (swappable), and .NET Aspire.

---

## Prerequisites

\`\`\`bash
dotnet workload install aspire
brew install ffmpeg
\`\`\`

---

## Running locally (standalone — everything in one process)

\`\`\`bash
dotnet restore
cd src/MediathekNext.AppHost
dotnet run
\`\`\`

Aspire dashboard at **http://localhost:15000**. The app defaults to `role=standalone`.

---

## Role system

The same binary (`MediathekNext.Worker`) runs in three roles.
Role is resolved in priority order — first match wins:

| Priority | Method | Example |
|---|---|---|
| 1 | CLI argument | `dotnet run -- --role worker` |
| 2 | Environment variable | `MEDIATHEK_ROLE=worker` |
| 3 | appsettings.json | `"Role": "worker"` |
| 4 | Default | `standalone` |

| Role | Does |
|---|---|
| `standalone` | Everything: migrations, catalog, API, downloads |
| `core` | DB owner, catalog refresh, API — no downloads |
| `worker` | Download execution only — scalable horizontally |

---

## Scaling with Docker Compose

\`\`\`bash
# Standalone (single machine, simple)
docker compose --profile standalone up

# Scaled out (core + multiple workers)
docker compose --profile distributed up
docker compose --profile distributed up --scale worker=3

# With observability
docker compose --profile distributed --profile observability up
\`\`\`

---

## Swapping the database

Change two config values — no code changes required:

\`\`\`json
{
  "Database": {
    "Provider": "postgres",
    "ConnectionString": "Host=localhost;Database=mediathek;Username=app;Password=secret"
  }
}
\`\`\`

Supported providers: `sqlite` (default), `postgres`, `mssql`

Uncomment the corresponding package in `MediathekNext.Worker.csproj` and `MediathekNext.Infrastructure.csproj`.

---

## EF Core migrations

\`\`\`bash
cd src/MediathekNext.Worker

dotnet ef migrations add <Name> \
  --project ../MediathekNext.Infrastructure \
  --startup-project .

dotnet ef database update \
  --project ../MediathekNext.Infrastructure \
  --startup-project .
\`\`\`

---

## Architecture

\`\`\`
src/
  Domain          — Entities, interfaces, enums (zero deps)
  Application     — Use cases, handlers, validators
  Infrastructure  — EF Core, repos, ffmpeg, MediathekView parser, polling service
  Api             — ASP.NET Core 10 Minimal API endpoints
  Web             — Blazor Server frontend
  Worker          — Single binary, role-switchable (standalone/core/worker)
    Roles/
      StandaloneRole.cs
      CoreRole.cs
      WorkerRole.cs
  AppHost         — .NET Aspire orchestration
  ServiceDefaults — OTEL, health checks, service discovery
\`\`\`

### How job dispatch works (no message broker)

1. `POST /api/downloads` → `StartDownloadHandler` inserts a `DownloadJob` row with `Status=Queued`
2. `DownloadPollingService` (running in `worker` or `standalone` role) polls every 5 seconds
3. Worker claims a job by calling `job.MarkClaiming(workerId)` and calling `SaveChanges()`
4. EF Core includes the `RowVersion` concurrency token in the `WHERE` clause automatically
5. If two workers race, one gets `DbUpdateConcurrencyException` and moves on — no locks, no broker, works on any SQL database

---

## API

\`\`\`
GET  /api/catalog/search?q=
GET  /api/catalog/episodes/{id}
GET  /api/catalog/shows
GET  /api/catalog/shows/{showId}/episodes
GET  /api/catalog/channels/{channelId}?contentType=
GET  /api/catalog/type/{contentType}?channelId=

POST   /api/downloads
GET    /api/downloads
GET    /api/downloads/{id}
DELETE /api/downloads/{id}
POST   /api/downloads/{id}/retry

GET /api/settings
PUT /api/settings
\`\`\`

OpenAPI: `/openapi/v1.json`
