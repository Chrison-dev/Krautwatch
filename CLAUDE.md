# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this project is

**MediathekNext** is a self-hosted browser and downloader for German public TV content (ARD, ZDF, etc.). It provides a REST API and Blazor Server web UI for browsing/searching episodes and downloading them locally via ffmpeg.

## Common commands

```bash
# Restore and build
dotnet restore
dotnet build

# Run tests
dotnet test

# Run a single test project
dotnet test tests/MediathekNext.Application.Tests/

# Run locally via .NET Aspire orchestrator (all services, dev dashboard at localhost:15000)
cd src/MediathekNext.AppHost && dotnet run

# Docker: standalone (all-in-one), distributed (core + workers), or with observability stack
docker compose --profile standalone up
docker compose --profile distributed up
docker compose --profile observability up

# EF Core migrations (run from src/MediathekNext.Worker)
dotnet ef migrations add <Name> --project ../MediathekNext.Infrastructure --startup-project .
```

System dependency: `ffmpeg` must be available on PATH for downloads.

## Architecture

### Project structure

```
Domain (zero deps) → Application → Infrastructure → Api/Web
                                                   └→ Worker (host, role resolver)
                                                   └→ AppHost (.NET Aspire, dev only)
ServiceDefaults (OTEL, health checks — referenced by Api, Worker)
```

- **Domain**: Entities (`Episode`, `Show`, `DownloadJob`, `AppSettings`), enums, and pure interfaces (`ICatalogProvider`, `IDownloadQueue`, `IRepository<T>`)
- **Application**: Use-case handlers (search, browse, download commands) with FluentValidation validators; depends only on Domain
- **Infrastructure**: EF Core (`AppDbContext`), TickerQ background jobs, ffmpeg wrapper, MediathekView catalog parser; implements Domain interfaces
- **Api**: ASP.NET Core Minimal API endpoints; communicates with Worker via WolverineFx messaging (`StartDownloadCommand`)
- **Web**: Blazor Server frontend; never touches Domain/Infrastructure directly — calls Api via `MediathekApiClient` (HttpClient)
- **Worker**: Single binary that resolves into one of three roles (see below)

### Role system

The same compiled `MediathekNext.Worker` binary runs as one of three roles, controlled by CLI arg > env var > config > default:

| Role | Responsibilities |
|------|-----------------|
| `standalone` | Everything in one process (default for dev/single-machine) |
| `core` | DB owner, catalog refresh, API host |
| `worker` | Download job execution only (horizontally scalable) |

Roles are registered in `src/MediathekNext.Worker/Roles/`.

### Download pipeline (three-phase TickerQ jobs)

1. **ResolveStreamJob** — detect HLS vs MP4, get content length
2. **DownloadStreamJob** — invoke ffmpeg, stream to `.part` temp file with progress tracking via stderr `time=` parsing
3. **FinaliseDownloadJob** — mux/merge if needed, move from temp to final location

**Distributed job coordination**: `DownloadJob` uses EF Core `RowVersion` (optimistic concurrency) to prevent two workers claiming the same job — no message broker needed.

### Key patterns

- **Database provider agnostic**: SQLite (default), PostgreSQL, MSSQL — swap via `appsettings.json`, no code changes
- **Clean architecture layers**: Domain has zero external dependencies; Application depends only on Domain; nothing depends on Infrastructure except the host
- **ffmpeg as remux only**: `-c copy` flag (no transcoding), fast and lossless

## Tech stack

- **.NET 10**, ASP.NET Core Minimal APIs, Blazor Server
- **EF Core 10** (migrations in `MediathekNext.Infrastructure`)
- **TickerQ** — background job scheduling with distributed locking
- **WolverineFx** — lightweight in-process/network message bus
- **OpenTelemetry** — structured logs, metrics, traces; Prometheus at `/metrics`
- **FluentValidation**, **xUnit**, **NSubstitute**, **Shouldly**

## Architecture decisions

Documented in `/docs/architecture/` (DR-001 through DR-007). Read these before making structural changes — they cover decisions on the role system, job pipeline, catalog format, and database provider strategy.