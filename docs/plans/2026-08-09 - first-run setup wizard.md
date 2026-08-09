# 2026-08-09 — First-run setup wizard

**Status:** proposed · implements [#54](../../../../issues/54)

#54 was written before several of its dependencies landed. Re-reading it against the code changes the
shape substantially, so this records what is actually left and how it should be built.

## What has changed since #54 was filed

| #54 says | Reality today |
|---|---|
| Authentication "does not exist (#48)" | **Local auth ships.** `/setup` creates the admin, `/login` signs in, cookie + `ClaimsPrincipal` throughout. Only OIDC is missing. |
| Parallel downloads "does nothing (#51)" | **Honoured** as of #89. |
| Sonarr/Radarr integration "does not exist (#4/#5/#6)" | **`*arr` instances ship** — CRUD, API keys, per-row connection test (#4). Only env-var bootstrap (#5) and reach-back (#6) are open. |
| Wizard lives at `/setup` | **`/setup` is already taken** by admin creation. |

So the wizard is now mostly a *flow over things that exist*, not a flow waiting on them. That is a much
smaller job than the issue implies — with one exception (egress, below).

## Decision 1 — the wizard **is** `/setup`, extended; not a new route

The existing `/setup` is anonymous, static-SSR, and gated on **two** conditions: no administrator exists
*and* the caller presents the startup token from the host log. That gate is precisely what #54 asks for
under "the security-sensitive part of this issue" — and it already exists, reviewed, with the reasoning
recorded in `docs/plans/2026-07-27 - authentication.md`.

Building the wizard at a second route would mean a second gate over the same claim-the-instance window.
Two gates on the one page that must not be claimable is how that window gets left open. So:

- `/setup` keeps its current behaviour and becomes **step 1** of the wizard.
- Later steps live at `/setup/{step}` and are **`[Authorize]`** — after step 1 the operator has a session,
  and an admin session is a strictly stronger gate than the one-time token.

The wizard therefore spans two authorization regimes on purpose: token-gated while no admin exists,
session-gated once one does. `PageAuthorizationSpecs` asserts the anonymous page list, so adding these
pages forces that decision to be made explicitly rather than by omission — keep it that way.

## Decision 2 — "initialised" is its own flag, not "an admin exists"

Today `SetupStateHandler.IsSetupRequiredAsync` infers first-run from the absence of an administrator.
That cannot express *"admin created, wizard abandoned halfway"*, which #54 explicitly requires to resume
rather than restart.

Add **`AppSettings.SetupCompletedAt`** (nullable). Null means the wizard has not been finished:

- the app root redirects to the wizard,
- the wizard resumes at the first incomplete step,
- finishing stamps it, and it never triggers again.

Nullable timestamp rather than a bool because "when was this instance set up" is worth knowing later and
costs the same column.

## Decision 3 — static SSR for step 1, interactive for the rest

Step 1 must stay static SSR: it writes the auth cookie, which needs `HttpContext` before the response
starts, and an interactive circuit cannot do that (same reason as `/login`).

Steps 2+ write no cookie and need live Test buttons, so they are `InteractiveServer`. Declaring
interactivity per page, not globally on `<Routes>`, is the existing convention.

## Decision 4 — the wizard is a *view over settings*, never a second store

#54 is explicit and it matters: every step must also be reachable from Settings afterwards. So each step
reuses the same Application handlers the Settings page uses (`SaveSettingsHandler`,
`SaveArrInstanceHandler`, `TestArrConnectionHandler`, …). The wizard owns navigation and explanation; it
owns no configuration state of its own.

## Scope

### PR 1 — the wizard, over what already exists

- [ ] `SetupCompletedAt` on `AppSettings` + migration.
- [ ] Wizard shell: step navigation, progress, back/next preserving state, resume at first incomplete step.
- [ ] Root redirect while uninitialised.
- [ ] **Welcome** — Krautwatch is an indexer + download client that Sonarr/Radarr drive; it is not a
      Mediathek browser (DR-010). The cheapest step to build and the one that prevents the most likely
      misunderstanding.
- [ ] **Database** — read-only: resolved provider/host, and whether migrations are applied. Editing the
      connection string from the UI is *not* included; the app cannot rewrite its own connection and
      restart itself, and pretending otherwise is worse than saying "configured by your deployment".
- [ ] **Administrator** — the existing `/setup` form, as step 1.
- [ ] **Downloads** — directory with a **writability check** (the current form accepts unwritable paths)
      and parallel downloads, now that #51 makes it real.
- [ ] **Sonarr / Radarr** — add instances with the existing Test, then show what to paste back: the
      Newznab URL, the SABnzbd URL, and the API key.
- [ ] **Done** — summary and a note that everything lives in Settings.
- [ ] Tests: gating (token before admin, session after), resume, that finishing stamps the flag, and that
      the root redirect stops once stamped.

### PR 2 — geo-restricted egress step

Deliberately separate, because it is **not** a UI job. `Download:ProxyUrl` and `Download:ProxyList:*`
bind from the **Downloader host's configuration**, not the database — so there is nothing for a UI to
write. Making that step real means moving egress settings into `AppSettings` and having the Downloader
read them from there, the way it already does for `DownloadDirectory`.

That is a behavioural change to a security-adjacent path with its own migration and precedence question
(does config still win over the stored value, as it does for the TVDB key?). It deserves its own review,
not a corner of a wizard PR.

### Not now

- **OIDC step** — blocked on #48. The wizard offers local only; the existing copy already says "you can
  switch to OIDC later".
- **Env-var bootstrap pre-fill** (#5) — the wizard should skip step 6 when instances arrive from the
  environment. Cheap to add once #5 exists; pointless before.

## Risks

- **The claim window is the whole risk in this issue.** Step 1's existing double gate is what closes it.
  Any change that makes a wizard step reachable before an administrator exists reopens it — which is why
  every later step is `[Authorize]` rather than "token or session".
- A wizard that writes its own copy of settings would rot against the Settings page. Decision 4 exists to
  prevent that and should be enforced in review.
