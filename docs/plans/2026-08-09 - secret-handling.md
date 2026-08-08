# 2026-08-09 — Secret handling: references instead of encryption

**Status:** proposed · supersedes the approach in [#60](../../../../issues/60)

#60 asks to encrypt `*arr` API keys at rest. This plan argues for a different mechanism that reaches the
same goal more cheaply, and records why — the issue itself says the key-management decision is the
substance and should not be made implicitly inside a PR.

## The question that prompted this

> Is the DB really the right place here? Shouldn't those sit in an `appsettings.json`? Long term we'd want
> something like Key Vault or OpenBao to inject those secrets.

Half right, and the half that is right points at a better design than encryption.

## What we actually have

Four credentials, not one problem:

| Credential | Lives in | Written by | Verdict |
|---|---|---|---|
| `Krautwatch:ApiKey` — inbound, `*arr` → us | configuration only (`ApiKeyGuard`) | operator, by hand | ✅ already right |
| `Download:ProxyUrl` — may embed `user:pass@` | configuration only | operator, by hand | ✅ already right |
| `ArrInstance.ApiKey` — outbound, us → Sonarr | **Postgres, plaintext** | **the UI, at runtime** | ⚠️ in scope |
| `AppSettings.TvdbApiKey` | **Postgres, plaintext** | **the UI, at runtime** | ⚠️ in scope — #60 does not mention it |

Two of the four are already config-only. The two that are not are precisely the two the UI writes, which
is the whole difficulty.

## Why not "just move them to appsettings.json"

Because #4 shipped runtime CRUD for `*arr` instances, and #54's setup wizard is built on the same
assumption. A container's `appsettings.json` is not writable at runtime, so config-only means deleting the
"add instance" UI and telling operators to edit a file and restart. That is a regression, not a
simplification.

## Why not encryption at rest

ASP.NET Core Data Protection (#60's option 1) is a reasonable default in general, but for this product it
buys less than it looks:

- **The key ring is the whole game, and self-hosters will get it wrong.** It has to be persisted to a
  mounted volume and kept *out* of the database backup, or the gain is notional — the dump and the key to
  it travel together. Telling people this in a README does not make them do it.
- **It introduces a failure mode we do not have today.** Key ring lost or rotated away means every stored
  key must be re-entered, and we would owe operators a per-instance "cannot decrypt, needs re-entry"
  state. That is real work to build and a bad afternoon to hit.
- **It needs a data migration** over existing rows.
- **It does not protect against a compromised host** — the app must be able to decrypt, so anything running
  as the app recovers the keys. It protects DB dumps, backups and stolen volumes. That is worth having, but
  it is a narrower win than "encrypted" sounds.

## Decision: the stored value may be a reference

Let the stored string be either a literal secret or a **pointer to one**, resolved at the point of use:

```
ApiKey = "abc123def456"           → literal, exactly as today (UI-entered)
ApiKey = "env:SONARR_API_KEY"     → read from the environment
ApiKey = "file:/run/secrets/sonarr" → read from a mounted secret file
```

An operator who manages secrets properly stores a reference, and **the database contains no secret at
all** — strictly better than encrypting it, with no key ring to lose. An operator who just wants the UI to
work types the key and gets today's behaviour, now as an informed choice rather than the only option.

Later, Key Vault / OpenBao is **a new scheme**, not a redesign: `vault:krautwatch/sonarr`.

### This generalises a pattern we already have

`TvdbApiKeySource` (`src/Infrastructure/Tvdb/TvdbApiKeySource.cs`) already does exactly this by hand for
one credential: configuration wins over the database, and the UI reports the key as managed elsewhere via
a `TvdbKeyOrigin` enum. We are turning a one-off into a mechanism, not inventing one.

### Resolve at the point of use, not in the repository

Resolution must **not** happen on repository read. The entity keeps the raw stored form, because:

- the settings page needs to show `env:SONARR_API_KEY` *as* a reference, so the operator can see what is
  wired — resolving first would hide that;
- a resolution failure must not break listing instances;
- round-tripping an edit would otherwise write the resolved secret back as a literal, silently converting
  a reference into stored plaintext. **This is the sharpest failure mode to avoid.**

Two call sites resolve:

- `src/Infrastructure/Arr/ArrHttpClient.cs:39` — before setting the `X-Api-Key` header.
- `TvdbApiKeySource.Current` — the database branch, so a stored TVDB key can be a reference too.

### Port and shape

Port in `Domain/Interfaces/` (Application needs it to describe state in the UI), adapter in Infrastructure:

```csharp
public enum SecretOrigin { Literal, Environment, File, Unresolved }

/// <summary>A stored secret, and where its value actually came from.</summary>
public record SecretResolution(string? Value, SecretOrigin Origin, string? Problem);

public interface ISecretResolver
{
    SecretResolution Resolve(string? stored);
}
```

- A literal whose text begins with a scheme we support is escapable as `literal:…`, for the operator whose
  real key genuinely starts with `env:`.
- `Unresolved` carries a `Problem` string naming the missing variable or unreadable path.

### Failure behaviour

A reference that does not resolve **fails loudly, per instance**. The connection test must say
*"SONARR_API_KEY is not set in this container"* — not authenticate with an empty string and report a 401
the operator cannot explain. This is #60's requirement, kept.

### The per-host subtlety, which must be documented

A reference resolves **in the process that uses it**. The Web host runs connection tests; if `*arr`
reach-back (#6) later runs in another host, that host needs the same variable or mounted file. Compose and
the self-hosting guide (#26) must say so, because "it tests fine but reach-back 401s" is otherwise
unexplainable.

### UI behaviour

- A **reference is not a secret** — show it verbatim (`env:SONARR_API_KEY`), so operators can see which
  variable is wired. Only literals get masked. This changes `ArrInstanceMapper.Mask`.
- Show the resolved origin and whether it currently resolves, reusing the `TvdbKeyOrigin` idea.
- The edit form's blank-means-unchanged rule is untouched.

### No data migration

Existing plaintext rows are literals and keep working unchanged. Nothing to migrate, nothing to
back-fill, no decrypt-failure state to design. This is the largest practical advantage over encryption.

## What this does and does not protect

Being explicit, because "we handle secrets properly now" is easy to over-claim:

- ✅ **Reference mode:** database dumps, backups, snapshots and stolen volumes contain **no secret**. This
  is the realistic leak path for a self-hosted app.
- ❌ **Literal mode:** unchanged from today. Plaintext in Postgres.
- ❌ **Neither mode** protects a compromised application host — the app must be able to read the secret, so
  anything running as the app can too. Same limitation encryption has.

So: reference mode is the hardened path, literal mode is the convenience path, and the docs must say which
is which rather than implying the feature makes the product secure.

## Scope

- [ ] `ISecretResolver` port + Infrastructure adapter (`env:`, `file:`, `literal:`, bare literal).
- [ ] Resolve at `ArrHttpClient` and in `TvdbApiKeySource`'s database branch.
- [ ] Guard against the round-trip hazard: an edit that leaves the key blank must never rewrite a
      reference as a resolved literal. Cover with a test.
- [ ] Surface origin + resolution state on the settings read models; stop masking references.
- [ ] Loud, specific failure for an unresolvable reference, surfaced through the connection test.
- [ ] Audit for disclosure: no resolved values in logs or exception messages.
- [ ] Document in the self-hosting guide (#26): the schemes, the per-host rule, and the honest threat
      model above.

## Explicitly out of scope

- Encrypting literal values. Available later if wanted — the resolver is where it would hook in — but it
  is not this change, and the reference path is the better answer for operators who care.
- `Krautwatch:ApiKey` and `Download:ProxyUrl`, which are already config-only and correct.
- Any Key Vault / OpenBao adapter. The point of this design is that it becomes a scheme when wanted.
