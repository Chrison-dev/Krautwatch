# 2026-07-27 — Sonarr/Radarr instance configuration UI (#4)

**Status:** planned, **blocked on #48 (authentication)** · **Milestone:** Foundation · **Implements:** DR-010
**Unblocks:** #6 (pull monitored series), #5 (env-var bootstrap), #54 step 6 (setup wizard), #12 (RSS scoped to monitored shows)

> **Sequencing decided 2026-07-27:** #48 authentication lands **first**. This page stores Sonarr/Radarr
> API keys, and `Presentation/Web` currently has no authentication whatsoever — putting credentials
> behind an open UI was judged unacceptable, so masking alone is not enough to proceed. See
> `2026-07-27 - authentication.md`. Everything below stands as planned once auth is in place.

DR-010 makes Sonarr/Radarr the drivers of Krautwatch, but **nothing in the system knows an `*arr`
instance exists**. There is no entity, no persistence, no client, and no UI. #4 is the foundation the
rest of the `*arr` integration stands on — most importantly #6, which needs a configured instance and
API key before it can fetch a monitored-series list.

## Current state

- `Application/Settings` holds download settings only, and its `GetSettingsHandler` /
  `SaveSettingsHandler` are **registered in DI but called by nothing** — there is no settings page in
  `Presentation/Web` at all (only Home, Search, Activity).
- `AppSettings` is a singleton row; instances are a *collection*, so they need their own table.
- The `Krautwatch:ApiKey` that `*arr` apps use to call **us** is config-only (`ApiKeyGuard`). That is the
  inbound direction and stays as-is here; this plan is about the **outbound** direction — our keys for
  calling *them*.

## Shape (three reviewable PRs)

### PR 1 — Model + persistence · `feat/arr-instance-model`

**Domain**
- `ArrInstance` entity: `Id` (Guid), `Name`, `Kind`, `BaseUrl`, `ApiKey`, `Enabled`,
  `CreatedAt`, and last-test outcome (`LastTestedAt`, `LastTestOk`, `LastTestMessage`) so the UI can
  show state without re-probing on every page load.
- `ArrKind` enum: `Sonarr` | `Radarr`.
- `IArrInstanceRepository` port (get all / by id / add / update / delete).
- `IArrClient` port — the outbound HTTP boundary. Starts with
  `Task<ArrSystemStatus> GetSystemStatusAsync(ArrInstance, CancellationToken)`; #6 extends the same
  port with the monitored-series call, which is why it is a port and not a UI helper.

**Infrastructure**
- `Persistence/ArrInstanceRepository` + `AppDbContext` mapping + EF migration `AddArrInstances`.
- Unique index on `BaseUrl` — #5 matches env-var bootstrap by base URL, so duplicates must be
  impossible at the schema level rather than by convention.

### PR 2 — Test Connection + the outbound client · `feat/arr-client`

- `Infrastructure/Arr/ArrHttpClient` implementing `IArrClient`:
  `GET {BaseUrl}/api/v3/system/status` with the `X-Api-Key` header, returning app name and version.
- `TestArrConnection` in `Application/Settings` — an **Action** (external IO per DR-009).
- Failure modes must be distinguishable, because "it doesn't work" is the single most common
  self-hosting complaint: unreachable host, TLS failure, 401 (bad key), 404 (wrong base path — e.g. a
  reverse-proxy subpath), and a 200 that isn't actually an `*arr` (wrong port).
- Persist the outcome to the last-test fields.

### PR 3 — The Settings page · `feat/settings-ui`

- `Presentation/Web/Components/Pages/Settings.razor` at `/settings`, plus a NavMenu entry.
- **Instances section:** table (name, kind, base URL, enabled, last test) with add / edit / delete and a
  per-row **Test** button.
- **Downloads section:** adopt the orphaned `GetSettingsHandler` / `SaveSettingsHandler` so download
  directory and concurrency finally have a UI. Note the concurrency value is currently inert (#51) —
  label it accordingly rather than implying it works.
- Validation via FluentValidation, matching `SaveSettingsRequestValidator`'s existing style.

## Decisions

### API keys are write-only in the read model

`*arr` API keys are credentials. Query DTOs return a **masked** key (`••••abcd`) and never the full
value; only the save path accepts a plaintext key. Editing shows an empty field meaning "unchanged".

This matters more than usual because **`Presentation/Web` currently has no authentication at all**
(#48). Until that lands, anyone who can reach the UI can reach this page. Masking limits it to
*writing* keys rather than *harvesting* existing ones — but it does not remove the exposure, and this
page raises the cost of leaving #48 undone. Storage is plaintext in Postgres; encryption at rest is
out of scope and belongs with #48's decisions.

### Test Connection is an Action that runs in a UI host

DR-009 says Actions are IO-driven and run on **Agents**. Test Connection is unavoidably IO-driven and
unavoidably synchronous — the operator clicks a button and expects an answer. So `Presentation/Web`
executes an Action in-process.

This is a deliberate, narrow deviation. The architecture tests still pass (they enforce layer
dependencies, not host/operation pairing), and the alternative — round-tripping a "test this instance"
message through the durable bus to an agent and polling for a result — is far more machinery than a
button warrants. Worth recording in CLAUDE.md so it reads as intentional rather than as drift.

### Instances live in the existing `Settings` slice

Not a new slice. CLAUDE.md already earmarks `Application/Settings` for "Sonarr/Radarr instances", they
are configuration, and a separate slice would need cross-slice access from the same page — which the
slice-isolation architecture test forbids.

## Out of scope

- **#6** monitored-series fetch — this plan provides the `IArrClient` port and a configured instance; the
  crawl work-list change is its own piece of work.
- **#5** env-var bootstrap — depends on this table existing.
- **#48** authentication, and encrypting keys at rest.
- Prowlarr-specific handling. Prowlarr is configured *pointing at us* (inbound) and needs no outbound
  instance record.

## Tests

- Validator specs (base URL well-formed and absolute, name required, key required on create).
- `ArrInstanceRepository` against real Postgres via the existing `PostgresFixture` collection.
- `ArrHttpClient` against a stubbed `HttpMessageHandler` covering each failure mode above — **not** a
  live test, since `Live.Tests` hits real broadcasters and there is no real Sonarr to point at.
- A masking spec: the query DTO must never carry a full API key.
