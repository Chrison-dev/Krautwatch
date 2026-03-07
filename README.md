# MediathekNext

A self-hosted application to browse and download content from German public TV channels (ARD, ZDF and more).

## Architecture

- **Frontend** — Blazor Server
- **API** — ASP.NET Core 10 Minimal API
- **Workers** — .NET Worker Services via Wolverine (scalable)
- **Core Worker** — Single instance, owns SQLite DB + catalog refresh
- **Orchestration** — .NET Aspire (dev) / Docker Compose (prod)
- **Observability** — Prometheus + Loki + Grafana

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Docker Desktop](https://www.docker.com/products/docker-desktop/)
- [.NET Aspire workload](https://learn.microsoft.com/en-us/dotnet/aspire/fundamentals/setup-tooling)

```bash
dotnet workload install aspire
```

### Run locally with Aspire

```bash
cd src/MediathekNext.AppHost
dotnet run
```

### Run in Docker (production)

```bash
docker compose up -d
```

## Project Structure

```plain
src/
  MediathekNext.Domain/         # Entities, interfaces, enums — no dependencies
  MediathekNext.Application/    # Use cases, commands, queries (MediatR/Wolverine)
  MediathekNext.Infrastructure/ # EF Core, catalog providers, download engine
  MediathekNext.Api/            # ASP.NET Core Minimal API
  MediathekNext.Web/            # Blazor Server frontend
  MediathekNext.Worker/         # Scalable download worker (ffmpeg)
  MediathekNext.CoreWorker/     # Single-instance worker (DB, catalog, settings)
  MediathekNext.AppHost/        # .NET Aspire orchestration
  MediathekNext.ServiceDefaults/# Shared Aspire service configuration

tests/
  MediathekNext.Domain.Tests/
  MediathekNext.Application.Tests/
  MediathekNext.Infrastructure.Tests/

docs/
  architecture/                 # Architecture Decision Records (ADRs)
  user-stories/                 # Epics and user stories
```

## Documentation

- [Architecture Decisions](docs/architecture/)
- [User Stories](docs/user-stories/)
- [Personas](docs/personas.md)

## License

TBD
