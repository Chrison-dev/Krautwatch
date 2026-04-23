# Database Migration — TickerQ Refactor

## What changed

The TickerQ refactor adds new columns to `DownloadJobs` and three new TickerQ tables.

## Generate migration

```bash
cd src/MediathekNext.Worker

dotnet ef migrations add TickerQIntegration \
  --project ../MediathekNext.Infrastructure \
  --startup-project .
```

## What the migration will contain

### DownloadJobs table — new columns
- `StreamType` (TEXT, nullable) — "HLS" or "MP4", populated by ResolveStreamJob
- `ContentLengthBytes` (INTEGER, nullable) — from HTTP Content-Length header
- `TempPath` (TEXT, nullable) — .part file path during download
- `StartedAt` (TEXT, nullable) — when Resolve phase began

### Dropped columns
- `WorkerId` — replaced by TickerQ's distributed locking
- `RowVersion` — replaced by TickerQ's distributed locking

### New TickerQ tables
- `TimeTickerEntities` — one-off scheduled jobs (download phases)
- `CronTickerEntities` — recurring cron jobs (catalog refresh, maintenance)
- `CronTickerOccurrenceEntities` — execution history per cron ticker

## Apply migration

```bash
cd src/MediathekNext.Worker

dotnet ef database update \
  --project ../MediathekNext.Infrastructure \
  --startup-project .
```

## TickerQ dashboard

Available at `/tickerq` (standalone and core roles only).

Default credentials (change in appsettings.json → TickerQ:Dashboard):
- Username: `admin`
- Password: `changeme`
