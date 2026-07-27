# 2026-07-27 — Authentication: pluggable scheme, simple built-in, OIDC as the target (#48)

**Status:** planned · **Milestone:** Foundation
**Blocks:** #4 (instance config UI — it stores `*arr` API keys), and therefore #6/#5/#54 step 6

Today `Presentation/Web` has **no authentication at all**, and the only protection anywhere is a single
optional instance API key (`Krautwatch:ApiKey`, enforced by `ApiKeyGuard`) on the `*arr`-facing host —
which, when unset, leaves everything open. #4 would put Sonarr/Radarr credentials behind that open UI,
so auth comes first.

The goal is not to reinvent identity the way the `*arr` apps do. Krautwatch should ship a deliberately
simple built-in option for someone running it on a home box, while delegating to a real identity
provider for anyone who already has one.

## The shape is not one interface — and this matters

The instinct is a single `IAuthenticationProvider` with `local` and `oidc` implementations behind it.
**That does not work**, and building it would produce a leaky abstraction:

- **Local credentials** are a *verification* concern: given a username and password, is this the admin?
  That fits a Domain port cleanly.
- **OIDC** is a *protocol* concern: redirects, an authorization code exchange, token validation,
  callback endpoints. All of it lives in ASP.NET Core's `OpenIdConnect` middleware. There is nothing
  meaningful left to put behind a Domain-level `AuthenticateAsync(username, password)` — OIDC never
  sees a password.

So the abstraction sits one level up, at **scheme selection in the Presentation composition root**:

```
Auth:Provider = none | local | oidc
        │
        ├─ local → cookie auth + ILocalCredentialStore (Domain port)
        │            └─ LocalCredentialStore (Infrastructure, EF + hashed password)
        │
        └─ oidc  → cookie auth + OpenIdConnect middleware (framework; no Domain port)
```

Both land on the **same cookie** and the same `ClaimsPrincipal`, so everything downstream —
`[Authorize]`, `AuthorizeRouteView`, "who am I" in the UI — is identical regardless of provider. That is
the real abstraction: the app depends on *an authenticated principal*, not on how it was obtained.

## Shape (three reviewable PRs)

### PR 1 — Local provider + lock the UI down · `feat/auth-local`

**Domain**
- `AdminAccount` entity: `Id` (singleton, like `AppSettings`), `Username`, `PasswordHash`,
  `CreatedAt`, `LastLoginAt`.
- `ILocalCredentialStore` port: does an admin exist, read it, create/update it. Framework-agnostic —
  **no ASP.NET types in Domain**, so hashing and cookies stay out of it.

**Infrastructure**
- `LocalCredentialStore` (EF) + migration `AddAdminAccount`.
- `IPasswordHasher` port + adapter over ASP.NET Core Identity's `PasswordHasher<T>`
  (PBKDF2-HMAC-SHA256, 100k+ iterations, per-user salt, versioned format). Deliberately **not**
  hand-rolled crypto, and deliberately not the full Identity stack — we need one hasher, not a user
  system.

**Presentation/Web**
- Cookie authentication; `CascadingAuthenticationState` + `AuthorizeRouteView`.
- `/login` page, sign-out, and the current user shown in the nav.
- **Fallback authorization policy: everything requires auth by default**, with `/login`, `/setup`,
  `/health` and `/alive` explicitly anonymous. Opt-out beats opt-in — a new page added later is then
  protected by default rather than accidentally public.

**First-run admin creation — the security-critical bit**
- `/setup` is reachable only while no admin exists, and is gated so it cannot be claimed by whoever
  reaches the box first (see decision below).
- Once an admin exists, `/setup` returns 404 and never reopens.

### PR 2 — OIDC · `feat/auth-oidc`

- `Auth:Provider = oidc` wires `AddOpenIdConnect` with authority, client id/secret and scopes from
  config, sharing the cookie from PR 1.
- Map an `admin` claim/role so the config pages have something to authorize against, with config for
  which claim carries it.
- Document Authentik / Keycloak / Authelia / Entra as worked examples in the self-hosting guide (#26).

### PR 3 — The `*arr` inbound key, revisited · `feat/arr-apikey`

Separate from human auth, and **cannot** be OIDC: Sonarr/Radarr/Prowlarr can only send an `apikey`
query parameter. So the machine surface stays key-based by protocol necessity, but improves:

- Generate a real key on first run instead of `Krautwatch:ApiKey` being unset-and-therefore-open.
- Surface it in the (now authenticated) UI so it can be copied into the `*arr` side, with rotation.
- Keep `t=caps` anonymous so Prowlarr can still probe the indexer — an explicit, documented carve-out.
- Decide whether `Auth:Provider = none` should even remain permitted, or require an explicit
  `Auth:AllowAnonymous = true` so "wide open" is a choice rather than a default.

## Decisions

### `Auth:Provider` defaults to `local`, not `none`

A fresh install should land on the `/setup` flow, not an open instance. `none` remains available for
someone deliberately fronting Krautwatch with forward-auth in a reverse proxy — a legitimate setup that
should not be blocked, but also should not be what you get by forgetting to configure anything.

### First-run `/setup` is gated by a startup token

Leaving `/setup` open until claimed is what most self-hosted apps do, and it is an
admin-account-takeover window: on a shared LAN, whoever loads the page first owns the instance.

The plan is a **setup token generated at startup and written to the host log** (the pattern Aspire's own
dashboard uses), which `/setup` requires. It is visible to whoever can read the container logs — i.e.
the operator — and to nobody else. Loopback-only was considered and rejected: it breaks the common case
of deploying to a homelab box and configuring it from a laptop.

### Password hashing is delegated, not written

`PasswordHasher<T>` from ASP.NET Core Identity: PBKDF2-HMAC-SHA256, per-user salt, versioned hash
format with built-in rehash-on-verify. Bringing in Argon2 via a third-party package would be defensible
but adds a dependency for no meaningful gain at this threat level.

### Rate-limit login

A single-admin login with no throttling is an offline-speed online guessing target. Simple fixed-window
limiter on `/login` per source IP — cheap, and its absence is the kind of thing that reads as negligence
in a self-hosted app.

## Out of scope

- Multiple users, roles beyond admin, invitations, password reset by email. A single admin plus OIDC
  covers the actual audience; a user system does not need inventing.
- Encrypting `*arr` API keys at rest. Once the UI is authenticated the marginal gain is small, and doing
  it properly needs a key-management story. Worth its own issue.
- 2FA for the local provider — the answer for anyone who wants it is OIDC.
- The **setup wizard** (#54). This PR delivers the minimum first-run step: create the admin. #54 wraps
  the wider guided flow around it and must reuse this gate rather than adding a second one.

## Tests

- `LocalCredentialStore` against real Postgres via the existing `PostgresFixture` collection.
- Hashing: verify round-trip, reject a wrong password, and confirm two hashes of the same password
  differ (salting actually happening).
- `/setup` gating: unreachable without the token; unreachable once an admin exists; a second POST
  cannot overwrite the first admin.
- Fallback policy: an authenticated-by-default page returns a redirect/401 anonymously, while `/login`,
  `/health` and `/alive` stay reachable — this is the regression that would silently expose the UI.
