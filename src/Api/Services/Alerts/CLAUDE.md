# CLAUDE.md — Services/Alerts/

The **condition-mirror** alert engine (M6·a, architecture.html §3·c) — the live "needs attention"
surface. `GET /api/v1/alerts?status=firing|resolved&since=24h` + the `alerts` WS topic
(`alert.raise`/`resolve`/`retract`). Built; the contract is frozen in `PLAN.md §6` (alert row) + `§8`
(M6·a log). This file is the local "what you must not break."

## What an alert is (and is NOT)

An alert mirrors a **condition**, not a task. The server raises it while the condition is true and
resolves it when the condition clears (self-heal or operator) — **the client never writes one**: no
complete, no dismiss, no PATCH. The feed trends toward empty ("all clear"); the durable, growing record
of *what fired* lives in **`/audit`**, never here. Alerts are **present-tense + mutable**; audit is
**past-tense + immutable** — they never overlap (§3·c). `resolution.actionId` is the one-way link from a
resolved condition to the audit action that fixed it.

## Locked decisions (do not relitigate)

- **Crash source ONLY at M6·a.** The single producer wired is the watchdog's supervision state, polled
  via kgsm-lib `IWatchdogClient.ListAsync()` (the C#↔engine chokepoint — **never a raw socket**). A
  `Desired="running"` instance with `Phase="restart-pending"` is a firing `warn`; `Phase="failed"` (the
  supervisor exhausted retries and **gave up**) is an `escalated` `danger`. Everything is measured from
  the kernel (`cgroup.events`) — **never fabricated**. Deferred (no honest source yet, like M6·b's
  reserved `reachable`): metric thresholds (`host-monitor`/`metrics` source), leaf-down (already on the
  `capabilities.patch` axis — a leaf is infrastructure, not a §3·c game-server condition), port-unreachable
  (no upstream prober). **Honest boundary:** the watchdog supervises **native** instances only — container
  crashes are out of scope until a Docker event source exists. Don't add a source whose signal you can't
  honestly measure.
- **The poll IS the authority, and the poll interval IS the raise debounce.** We do **not** event-fast-path
  a raise — a crash that recovers faster than one poll tick is never seen down, so it never fires (exactly
  §3·c's "don't fire on a blip"). Firing on every transient crash would be the noise the dwell exists to
  prevent. The **only** event integration is the actionId bridge below.
- **Resolve is probation-gated (api-owned); escalate is mirrored (watchdog-owned).** A cleared condition is
  resolved only after it stays clear for `ResolveProbation` (30s) — measured from the **first clear
  observation**, so a crash-loop (crash→start→crash) re-arms the clock and **never flaps** the feed.
  Escalation is **not** re-derived: `Phase="failed"` IS the watchdog's own circuit-break → `escalated:true`,
  which **never auto-resolves** (an unfixable problem grows louder, never hides).
- **Stable, condition-derived id.** `crash:<serverId>` — a re-fire **upserts** the same record and an
  escalation **re-pushes** it (full record on `alert.raise`). **Never** a fresh per-raise id.
- **In-memory, ages off, rebuilds on restart — never fabricates on a blind tick.** No EF table (the durable
  record is `/audit`). The rear-view holds resolved records for `ResolvedRetention` (24h) then drops them.
  On an API restart the firing set is reconstructed from the next poll (the watchdog state is **queryable**,
  not an unreplayable event). If a poll **fails** (unreachable/timeout) the tick is **skipped** — the firing
  set persists; we **never** resolve or retract on the absence of an answer (honest-unknown). A condition
  that fired-and-resolved while the API was down is simply absent — the transition still lives in `/audit`.
- **The alert↔audit bridge is a hand-off, not a second socket.** `AlertEngine.NoteRecoveryAction(serverId,
  evt_id)` is called by `KgsmAuditConsumer` **after** it writes a `server.start`/`server.restart` audit row
  (so the id exists); the poll stashes it and, when a crash later resolves because the server recovered,
  stamps it as `resolution.actionId`. The poll can't learn an audit id on its own — this is the sole reason
  the engine touches the event path, and it's lock-free (a `ConcurrentDictionary`, not shared alert state).
  **The bridge and its limit:** the watchdog's autonomous crash-restart now emits `instance_restarted`
  (`system`/`system`, kgsm-watchdog `d4b453f`) → a `server.restart` row through the same `WriteServerAndBridge`
  handler, so a **pure auto-heal bridges** `resolution.actionId` once that row is consumed (within the resolve
  probation) — alongside an **operator/api** start|restart recovery. The watchdog's **boot-autostart** also
  emits (`instance_started`, `system`/`system`) → it is **audited** as a `server.start` row but **NOT bridged**:
  `KgsmAuditConsumer.IsRecoveryAction` excludes the system-origin start, because a fresh boot bring-up is not a
  crash recovery — letting it bridge could stamp a stale id on a later crash whose own recovery event dropped
  (honest-null over a plausible-but-wrong link). **Still null (never fabricated):** a **stop-cleared** crash (a
  stop is not a recovery), and a crash that resolves before its `server.restart` row is consumed (an honest
  race). **Pre-existing broader limit (not closed here):** `_lastStartAction` is "last start/restart ever," not
  crash-episode-scoped, so a dropped recovery event for an *operator* start can still stamp a stale operator id;
  the root-cause fix (episode-scoping/clearing `_lastStartAction`) is a separate change. *(The watchdog-emit
  halves are live-validated on the wire; the full on-host bridge round-trip with a running API is owed.)*

## WS message contract (architecture.html §3·c)

- `alert.raise` → the **full `Alert`** record (status `firing`). Re-pushed to flip `escalated`/`attempts`.
- `alert.resolve` → **`{ id, resolution }`** (`AlertResolved`). Client stamps `resolvedAt`, moves to the
  rear-view; the authoritative `resolvedAt` is on the REST resolved record.
- `alert.retract` → **`{ id }`** (`AlertRetracted`). Subject gone (instance uninstalled) → no rear-view.
- **Coalesce key = the alert id** (`StreamProtocol.AlertEntityKey`), so all three kinds for one condition
  share a slot — a `resolve`/`retract` correctly supersedes a queued `raise` (the `ServerPatch`/
  `ServerRemoved` precedent, **not** the audit per-append unique key). A torn-down slow client re-hydrates
  via `GET /alerts` (§3·j), so coalescing never loses durable truth.

## Invariants when you touch this

- **`Tick` is the single writer of alert state**, on the one poll-loop thread; the controller reads the
  volatile immutable snapshot. `_lastStartAction` is the only cross-thread state (concurrent). Keep it that
  way — don't mutate `_firing`/`_resolved`/`_clearSince` off the loop, or you reintroduce a lock.
- **Always-on, not subscriber-gated** (like `LeafHealthMonitor`, unlike the metric pumps): `GET /alerts`
  must serve fresh truth regardless of WS subscribers. With no watchdog provisioned the loop logs once and
  serves an **empty** feed (degrade gracefully — never a 500).
- **Severity is the §3·c subset** (`danger|warn|info`) — no `success` (a firing condition is never a
  success). `resolution.by` is **always `system`** (the server observed the clear, never the client).
- **`anchor.surface`** is a best-effort deep-link hint (`server` for a crash); the frontend always has
  `serverId`/`hostId` to route from if it doesn't recognize the surface. Confirm the surface vocabulary at
  the frontend gate before adding values.

## Auth (M4·a)

`GET /alerts` is `[Authorize(Policy = viewer)]` and the `alerts` WS topic rides the viewer-gated
`/stream` socket — a core read surface, consistent with `/audit`.
