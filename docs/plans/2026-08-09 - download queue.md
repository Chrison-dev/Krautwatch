# 2026-08-09 — Download queue: concurrency and priority

**Status:** proposed · implements [#51](../../../../issues/51)

#51 describes two independent gaps. They ship as **two PRs**, in this order:

1. **Honour `MaxConcurrentDownloads`** — the setting is exposed, validated and editable, and does
   nothing. A UI that lies is worse than a missing feature, so it goes first.
2. **Priority and reordering** — new capability, larger surface.

Splitting keeps each reviewable and gives two honest changelog lines instead of one vague one.

## Decision 1 — `Priority` column, not `QueuePosition`

An `int Priority` (higher runs sooner, default `0`), with `CreatedAt` as the tie-break.

Rejected explicit `QueuePosition` because every reorder would have to renumber its neighbours: N writes
per move, and two concurrent enqueues can race into the same position. Sparse priority makes each move a
**single-row write**:

| Action | Write |
|---|---|
| Move to top | `Priority = min(queued priority) - 1` |
| Move to bottom | `Priority = max(queued priority) + 1` |
| Move up / down | swap `Priority` with the adjacent queued job |

The cost is that there is no stable "this is job #3" number to show. #51 only requires move-to-top and
move-to-bottom as a minimum, and a queue ordered visually top-to-bottom communicates position perfectly
well without one.

Existing rows default to `0`, so today's pure-`CreatedAt` order is preserved exactly.

## Decision 2 — the concurrency limit is **per-process**

As #51 suggests. A global cap would mean counting live claims across processes and coordinating on every
claim — real complexity for a case that does not currently exist: the shipped compose runs exactly **one**
`agent-downloader`, so per-process and global are the same number today.

This must be *documented in the UI*, not just in code, or scaling the agent to two replicas silently
doubles the limit.

### How, without a new locking primitive

The existing claim is already an atomic compare-and-set (`ExecuteUpdate` on `Status == Queued`), so N
callers are safe as-is. The worker becomes a supervisor:

```
loop:
  desired = MaxConcurrentDownloads          # re-read every pass, so a UI edit takes effect
  while inFlight.Count < desired:
      job = TryClaimNext()                  # atomic; nothing new needed
      if job is null: break
      inFlight.Add(Run(job))                # own DI scope per job
  if inFlight.Count == 0: wait IdleDelay
  else: await WhenAny(inFlight)             # free a slot as soon as one finishes
```

Re-reading `desired` each pass is what satisfies #51's "react to the setting changing at runtime" without
an invalidation channel from Application into the agent.

**Each concurrent run gets its own DI scope.** The current code shares one scope between claim and run;
`DbContext` and the scoped repositories are not thread-safe, so sharing a scope across N runs would be a
data race. This is the one genuine hazard in the change.

`WorkerId` stays **per-process**, not per-loop: it identifies the process for startup stale-reclaim, and
making it per-loop would leave orphaned rows after a crash if the loop count changed.

## Decision 3 — yes, accept and report SABnzbd priority

`SabnzbdEndpoints` currently ignores incoming priority and reports a hardcoded `"Normal"`.

Accepting it is a few lines and directly addresses the motivating complaint in #51 — a manual grab buried
behind an RSS-Sync season pack. Sonarr already sends a higher priority for interactive grabs, so honouring
it makes the queue behave the way its user expects without anyone touching our UI.

SABnzbd's scale maps onto ours:

| SAB | Meaning | Our `Priority` |
|---|---|---|
| `-2` | Paused | *not mapped* — we have no paused state; treated as Low |
| `-1` | Low | `-1` |
| `0` | Normal | `0` |
| `1` | High | `1` |
| `2` | Force | `2` |

Reporting the real value back replaces the hardcoded string, so the queue Sonarr displays matches reality.

**Paused is deliberately not implemented.** A paused state is a queue feature in its own right, not a
priority, and pretending `-2` pauses something would be a worse lie than the one being fixed.

## Scope

**PR 1 — concurrency**
- [ ] Supervisor loop in `Agents/Downloader/DownloadWorker.cs`, own scope per job.
- [ ] Read `MaxConcurrentDownloads` from settings each pass, clamped to a sane floor of 1.
- [ ] Say "per process" where the setting is edited.
- [ ] Tests: N runs concurrently; a raised/lowered limit is picked up; one failing job does not stop the
      supervisor or the others.

**PR 2 — priority**
- [ ] `Priority` on `DownloadJob` + EF migration defaulting to `0`.
- [ ] Claim orders by `Priority` descending then `CreatedAt`; index to match.
- [ ] Reorder use-cases in `Application/Downloads` (top / bottom / up / down), `Queued` jobs only.
- [ ] Controls on `Activity.razor`.
- [ ] SABnzbd: accept incoming priority on add, report the real value in `queue`.
- [ ] Tests: ordering, reorder operations, that a non-`Queued` job cannot be reordered.

## Out of scope

- A paused state (see Decision 3).
- A global cross-process concurrency cap (see Decision 2).
- Per-show or per-quality scheduling policy — no evidence anyone wants it.
