# CLAUDE.md — Services/Alerts/

The **condition-mirror** alert engine (M6·a, architecture.html §3·c) — the live "needs attention"
surface. `GET /api/v1/alerts?status=firing|resolved&since=24h` + the `alerts` WS topic
(`alert.raise`/`resolve`/`retract`). Built; the contract is frozen in `PLAN.md §6` (alert row) + `§8`
(M6·a log). This file is the local "what you must not break."

**Increment 1 BUILT + unit-tested (2026-06-30):** a second producer — the metrics-threshold source — is
folded into this same engine per `metrics-threshold-alerts-plan.md`, so a sustained host or per-server
metric breach raises/resolves through this **same** `/alerts` REST + `alerts` WS surface, no new endpoint,
no protocol change. Build 0-warn, full suite 474/474 (+10 `TickMetrics` tests in
`tests/Api.Tests/MetricsThresholdAlertTests.cs`) + **LIVE-VALIDATED 2026-07-01** (dev build on `:8098` vs the real
monitor: `metric:host-mem` danger raise + `metric:srv-pids:factorio-test` warn raise, then a `factorio-test` stop →
vanished-row resolve `by:system`/`actionId:null`). **Still UNCOMMITTED and not deployed to `:8097`.** The locked
decisions below cover both the crash source (shipped) and the metrics-threshold source.

**Two things you must not break (the unit tests caught both during the build):**
- **Bind `MetricsThresholds:Rules` through the mutable `ThresholdRuleBinding` DTO, never the positional
  `ThresholdRule` record directly.** The config binder cannot construct a record whose `double? Danger` ctor
  param has no value and silently returns an empty list — so a single warn-only rule (`danger` absent/null,
  like the default `srv-*` rules) would drop the operator's whole custom policy back to `Default`. See
  `ApiOptions.LoadThresholdPolicy`.
- **The crash `Tick` resolve/retract loop must stay scoped to `crash:` ids** (`CrashIdPrefix`). Crash and
  metric alerts share `_firing`; without the guard a watchdog poll retracts/resolves a live `metric:` alert
  (its serverId isn't in the watchdog's `present`/`firingNow` sets). `TickMetrics` is symmetrically scoped to
  `metric:` ids.

## What an alert is (and is NOT)

An alert mirrors a **condition**, not a task. The server raises it while the condition is true and
resolves it when the condition clears (self-heal or operator) — **the client never writes one**: no
complete, no dismiss, no PATCH. The feed trends toward empty ("all clear"); the durable, growing record
of *what fired* lives in **`/audit`**, never here. Alerts are **present-tense + mutable**; audit is
**past-tense + immutable** — they never overlap (§3·c). `resolution.actionId` is the one-way link from a
resolved condition to the audit action that fixed it.

## Locked decisions (do not relitigate)

- **Crash source (M6·a, shipped) + metrics-threshold source (increment 1, being wired).** The
  watchdog-crash producer is unchanged: polled via kgsm-lib `IWatchdogClient.ListAsync()` (the
  C#↔engine chokepoint — **never a raw socket**). A `Desired="running"` instance with
  `Phase="restart-pending"` is a firing `warn`; `Phase="failed"` (the supervisor exhausted retries and
  **gave up**) is an `escalated` `danger`. Everything is measured from the kernel (`cgroup.events`) —
  **never fabricated**. A **second producer is now being folded in** (per
  `metrics-threshold-alerts-plan.md`): sustained host/per-server metric breaches read from the monitor
  `Snapshot`, populating the previously-reserved `host-monitor`/`metrics` `AlertSource` values — see the
  dedicated bullets below for the locked shape. Still deferred (no honest source): leaf-down (already on
  the `capabilities.patch` axis — a leaf is infrastructure, not a §3·c game-server condition),
  port-unreachable (no upstream prober). **Honest boundary:** the watchdog supervises **native** instances
  only — container crashes are out of scope until a Docker event source exists. Don't add a source whose
  signal you can't honestly measure.
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
  (honest-null over a plausible-but-wrong link — though episode-scoping below now also catches this). **Still
  null (never fabricated):** a **stop-cleared** crash (a stop is not a recovery), and a crash that resolves
  before its `server.restart` row is consumed (an honest race). **The bridge is episode-scoped (root-cause
  closed).** `_lastStartAction` stashes the action's audit-row timestamp, and `BuildResolution` honors it only
  when it **post-dates that crash's raise** (`action.At >= RaisedAt`). So a dropped recovery event can no
  longer let a stale "last start/restart ever" — operator OR system — mislink a later, unrelated crash: the
  resolution is honest `null` instead. Soundness rests on one invariant: kgsm/watchdog emit lifecycle events at
  operation **completion** (server up), never initiation, so a real recovery's timestamp is always at/after the
  poll that observed the server *down* (single-host → both share a wall clock). This subsumes the
  `IsRecoveryAction` boot-autostart exclusion above (a boot start's timestamp predates any later crash anyway),
  which is now belt-and-braces. *(The watchdog-emit halves are live-validated on the wire; the full on-host
  bridge round-trip with a running API is owed.)*

### Metrics-threshold source (increment 1, being wired — `metrics-threshold-alerts-plan.md`)

- **Sources: `host-monitor` (host-scope rules) + `metrics` (per-server rules).** Both previously reserved on
  `AlertSource` (no honest source), now populated by a sustained-breach reconcile pass over the monitor
  `Snapshot`. New `AlertSurface.Host` anchors a host-scope alert to the host (`tab:"performance"`); a
  server-scope metric alert keeps `AlertSurface.Server`, same as crash.
- **Policy = appsettings + env, not DB-backed, at increment 1.** A baked-in `MetricsThresholdPolicy.Default`
  in code; an optional `MetricsThresholds` config section overrides it **wholesale**; env wins per the
  ecosystem convention. DB-backed, panel-editable policy is **increment 2** — out of scope here. **Caveat to
  state plainly, not a missing feature:** tuning a threshold or enabling a per-server rule means editing
  `appsettings.json`/the env file and **restarting the API** — that is the expected increment-1 UX.
- **Scope: host rules ship ON, per-server rules ship OFF.** Host rules are universal `%`-based thresholds
  (disk/mem/swap/load/temp) — safe defaults on any host. Per-server rules are absolute byte/CPU/pid
  thresholds — game-specific, so they ship disabled; an operator opts a rule in per-server via config.
- **Alert id namespace: `metric:<ruleKey>[:<ref-or-serverId>]`** — a distinct namespace from `crash:<serverId>`,
  so the two sources can never collide on an id. A rule that fans out (per-mount disk, per-sensor temp)
  yields one observation — and one alert id — per target (e.g. `metric:host-disk:/` and
  `metric:host-disk:/data` as two independent alerts).
- **Anti-flap has two dwells, not crash's one.** Crash never needed a fire-dwell — its poll interval is its
  own debounce. A metric value can spike, so a rule must breach for `FireForSec` (tracked per-id in a new
  `_breachSince` map) before it raises. Clearing reuses the existing clear-probation (`ClearForSec`) but adds
  a **hysteresis deadband** (`ClearMargin`): the value must drop `ClearMargin` *below* `Warn` before the clear
  clock even starts, so a value hovering right at the threshold can't flap the feed.
- **Honest-unknown, metrics edition.** Monitor down (`snap == null`) → **skip the metric pass entirely** for
  that tick — never resolve or retract a metric alert on absence (mirrors crash's blind-poll honesty). A
  null field (a nullable per-server metric), a null `Cpu.Info`, or an empty `Sensors` array → that rule is
  **not evaluable** on that tick (hold: never fires, never advances the clear). A server row that **vanishes
  from a *non-null* snapshot** (the monitor is up, this row is just gone) is treated as cleared and resolves
  after the normal clear-dwell — distinct from a monitor blackout, which never resolves anything.
- **`resolution.actionId` is ALWAYS `null` for a metric alert.** The actionId↔audit bridge above
  (`NoteRecoveryAction`) is crash-specific — a metric alert clears because the measured value receded, not
  because an operator or system action ran. `resolution.by` still stays `system` (the server observed the
  clear, never the client).
- **Metric `danger` ≠ `escalated`.** Crash's `escalated` means "the supervisor gave up, never auto-resolves."
  A metric in the danger band still auto-resolves once it recedes below the clear margin, so metric alerts
  keep `Escalated = false` **always** — severity alone carries how bad it is, never `escalated`.
- **Folded into the existing `AlertEngine` — the single-writer invariant below still holds.** `Tick` (crash)
  keeps its exact current signature and behavior (so `AlertEngineTests` pass unchanged); the new
  `internal TickMetrics(Snapshot? snap, DateTimeOffset now)` is the metric seam, run on the **same**
  poll-loop thread. Both end by calling the **one** shared `RebuildSnapshot()`, which projects `_snapshot`
  from the full `_firing`/`_resolved` (crash + metric together) — order-independent, one authority, one
  retention, no second engine.

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

- **`Tick` and `TickMetrics` are the only writers of alert state**, both on the one poll-loop thread (never
  concurrently — `PollAsync` calls them sequentially each tick); the controller reads the volatile
  immutable snapshot. `_lastStartAction` is the only cross-thread state (concurrent). Keep it that way —
  don't mutate `_firing`/`_resolved`/`_clearSince`/`_breachSince` off the loop, or you reintroduce a lock.
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
