# CLAUDE.md — Services/Alerts/

The **condition-mirror** alert engine (architecture.html §3·c) — the live "needs attention"
surface. `GET /api/v1/alerts?status=firing|resolved&since=24h` + the `alerts` SSE topic
(`alert.raise`/`resolve`/`retract`). The contract is frozen in `PLAN.md §6` (alert row).
This file is the local "what you must not break."

**Three producers, one engine.** The watchdog crash source and the engine update-availability source are
polled by this API; the metrics-threshold source is **mirrored from kgsm-monitor**, which evaluates the
rules itself. All three share one `_firing`/`_resolved` set, one snapshot, one retention and one REST + SSE
surface. The locked decisions below are grouped by producer.

**The one thing to check before touching the shared state:** each producer's resolve/retract sweep is
**scoped to its own id prefix** — `crash:`, `metric:`, `update:`. They share `_firing`, so without those
guards a watchdog poll retracts a live metric alert (its target is not in the watchdog's `present` set) and
a condition tick resolves a live crash alert.

## What an alert is (and is NOT)

An alert mirrors a **condition**, not a task. The server raises it while the condition is true and
resolves it when the condition clears (self-heal or operator) — **the client never writes one**: no
complete, no dismiss, no PATCH. The feed trends toward empty ("all clear"); the durable, growing record
of *what fired* lives in **`/audit`**, never here. Alerts are **present-tense + mutable**; audit is
**past-tense + immutable** — they never overlap (§3·c). `resolution.actionId` is the one-way link from a
resolved condition to the audit action that fixed it.

## Locked decisions (do not relitigate)

- **Crash source.** Polled via kgsm-lib `IWatchdogClient.ListAsync()` (the C#↔engine chokepoint — **never a
  raw socket**). A `Desired="running"` instance with `Phase="restart-pending"` is a firing `warn`;
  `Phase="failed"` (the supervisor exhausted retries and **gave up**) is an `escalated` `danger`. Everything
  is measured from the kernel (`cgroup.events`) — **never fabricated**. **Honest boundary:** the watchdog
  supervises **native** instances only, so container crashes are out of scope until a Docker event source
  exists. Still deferred for want of an honest source: leaf-down (already on the `capabilities.patch` axis —
  a leaf is infrastructure, not a §3·c game-server condition) and port-unreachable (no upstream prober).
  Don't add a source whose signal you can't honestly measure.
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
  **The bridge and its limit:** the watchdog's autonomous crash-restart emits `server.restarted`
  (`system`/`system`) → a `server.restart` row through the same `WriteServerAndBridge`
  handler, so a **pure auto-heal bridges** `resolution.actionId` once that row is consumed (within the resolve
  probation) — alongside an **operator/api** start|restart recovery. The watchdog's **boot-autostart** also
  emits (`server.started`, `system`/`system`) → it is **audited** as a `server.start` row but **NOT bridged**:
  `KgsmAuditConsumer.IsRecoveryAction` excludes the system-origin start, because a fresh boot bring-up is not a
  crash recovery — letting it bridge could stamp a stale id on a later crash whose own recovery event dropped
  (honest-null over a plausible-but-wrong link). **Still
  null (never fabricated):** a **stop-cleared** crash (a stop is not a recovery), and a crash that resolves
  before its `server.restart` row is consumed (an honest race). **The bridge is episode-scoped.**
  `_lastStartAction` stashes the action's audit-row timestamp, and `BuildResolution` honors it only
  when it **post-dates that crash's raise** (`action.At >= RaisedAt`) — a dropped recovery event therefore
  never lets a stale "last start/restart ever" — operator OR system — mislink a later, unrelated crash: the
  resolution is honest `null` instead. Soundness rests on one invariant: kgsm/watchdog emit lifecycle events at
  operation **completion** (server up), never initiation, so a real recovery's timestamp is always at/after the
  poll that observed the server *down* (single-host → both share a wall clock).

### Metrics-threshold source — mirrored from kgsm-monitor

- **The monitor detects; this engine presents.** kgsm-monitor evaluates the threshold rules against every
  sample it takes and publishes the verdict as `Snapshot.Conditions` (`ConditionReading`); `TickConditions`
  turns each one into a `metric:<ruleKey>[:<ref-or-serverId>]` alert. **Do not reintroduce a threshold
  comparison, a dwell, or a policy here.** The API scrapes every 5s against a 1 Hz sampler, so a
  "sustained breach" it decided for itself would be a claim about a window it saw a fifth of — that is the
  defect the split exists to remove, and re-adding either half recreates it.
- **What this engine contributes is everything the leaf deliberately does not know:** `AlertSource`
  (`host-monitor` for `scope:"host"`, `metrics` for `scope:"server"`), `AlertSeverity` (from the condition's
  band), the `AlertAnchor` deep-link, and the words on the card (`ConditionDisplay`). The monitor's whole
  vocabulary on this side is two band names and two scope names.
- **Present means firing; absent means resolved, immediately.** The monitor publishes breaching conditions
  only, having already run its clear dwell. A probation here would delay a recovery it already verified
  against every sample. This is the one place the metric source and the crash source deliberately differ —
  crash resolution IS probation-gated, because the watchdog poll is this API's own and carries no dwell.
- **The alert id is derived from rule + target, never from the monitor's `episodeId`.** An episode id
  changes every time a condition clears and recurs; the feed's contract is that one condition keeps one id
  so a re-fire upserts the card an operator is already looking at. The episode id is the monitor's identity
  for one continuous breach and is what the audit rows dedup on — a different question.
- **Re-push only on a changed record.** A condition can last hours and the loop polls every few seconds; a
  frame per scrape to every open browser is what the `Severity`/`Title`/`Detail` comparison prevents.
- **Honest-unknown, and the distinction is the point.** `snap == null` (monitor down) holds every metric
  alert unchanged. An empty `Conditions` array means all-clear; no frame at all means nobody knows. Never
  collapse the two.
- **`resolution.actionId` is ALWAYS `null` for a metric alert.** The `NoteRecoveryAction` bridge is
  crash-specific — a threshold clears because the measured value receded, not because anybody acted.
  `resolution.by` stays `system`.
- **Metric `danger` ≠ `escalated`.** Crash's `escalated` means "the supervisor gave up, never auto-resolves".
  A metric in the danger band still resolves once it recedes, so metric alerts keep `Escalated = false`
  always — severity alone carries how bad it is.
- **`TickConditions` is scoped to `metric:` ids** and the crash `Tick` to `crash:`, symmetrically. They share
  `_firing`; without the guards a watchdog poll retracts a live metric alert (its target is not in the
  watchdog's `present` set) and a condition tick resolves a live crash alert.
- **The detail line reports `windowMax`.** The headline value is whatever the metric read when the frame was
  built; the peak is what actually justified the alarm, and on a moving value those are not the same number.
  It is only stated when it differs from the headline.

### Engine source — update availability

- **Source `engine`, id namespace `update:<serverId>`, severity `info`.** A third producer,
  `TickUpdates`, mirrors update availability into the feed: firing while a newer game build exists,
  resolved when the update is applied. `Escalated` is always `false` and `Attempts` always `0` —
  nothing is retrying and nothing gives up; the condition waits for a person.
- **It measures nothing, and that is the point.** kgsm establishes the fact (the scheduler's sweep
  runs the networked check, the engine records what it found beside the instance), so this pass reads
  it off `InstanceCache.Statuses` — the same fast status the roster is built from. **Never add a probe,
  a version comparison or an upstream call here** — read `InstanceCache.Statuses` and nothing else.
- **Neither dwell applies, deliberately.** The metric fire-dwell exists because a value can spike and an
  update record cannot; the clear-probation exists because a crash-loop flaps and an applied update does
  not. A raise and a clear both take effect on the tick that observes them.
- **Three states, not two.** `UpdatesAvailable` is `null` until something has checked — that HOLDS, it
  does not clear. Only a measured `false` resolves. A non-measured `Reading` holds, and a cache whose
  last engine read failed skips the pass entirely (`InstanceCache.EngineRead`) — the same honest-unknown
  posture as a blind watchdog poll.
- **An uninstall retracts, it does not resolve.** A pending update on an instance that no longer exists
  was never fixed. Like the other two passes, the sweep is scoped to its own id prefix (`update:`) —
  `_firing` is shared, and the crash/metric rows are theirs to reconcile.
- **The loop runs even with nothing provisioned.** kgsm is this API's base dependency, so the engine
  source needs no capability; a host with no engine configured leaves the cache empty, which produces no
  alerts rather than a wrong feed.

## What a condition offers to do about itself

A firing record carries `actions[]` — the operations a surface may draw a button from
(`AlertActionCatalog`). Three rules hold it together:

- **The verb comes from `Services/Actions/ConditionActions.cs`, which the push surface reads too.** The
  same condition described on a lock screen and on a card must not be answered differently, and the crash
  pair is where a divergence would hurt most: reversed, each button asks for exactly what is already
  happening. `AlertActionCatalog` decides only which conditions this feed's producers correspond to; the
  wording is each surface's own (a lock screen says "Update now" where a card says "Update"), which is why
  no label is on the DTO.
- **An offer is a policy, not a permission, and nothing is staged.** It says the condition is the kind of
  thing that verb answers — not that the caller may run it or that the target accepts it right now. The
  panel applies its own gates at render and `POST /servers/{id}/commands` applies them again at the click,
  so an offer whose target is running renders as a disabled button that says why. A push button is staged
  behind a handle because a service worker holds no session; an alert card is drawn inside an
  authenticated panel and issues the command itself.
- **A threshold breach offers nothing, and a resolved record offers nothing.** A number over a line names
  no cause, so every verb available would be a guess at which; and a cleared condition has nothing left to
  do about it. `Actions` is `null` on every resolved record.

## Stream message contract (architecture.html §3·c)

- `alert.raise` → the **full `Alert`** record (status `firing`). Re-pushed to flip `escalated`/`attempts`.
- `alert.resolve` → **`{ id, resolution }`** (`AlertResolved`). Client stamps `resolvedAt`, moves to the
  rear-view; the authoritative `resolvedAt` is on the REST resolved record.
- `alert.retract` → **`{ id }`** (`AlertRetracted`). Subject gone (instance uninstalled) → no rear-view.
- **Coalesce key = the alert id** (`StreamProtocol.AlertEntityKey`), so all three kinds for one condition
  share a slot — a `resolve`/`retract` correctly supersedes a queued `raise` (the `ServerPatch`/
  `ServerRemoved` precedent, **not** the audit per-append unique key). A torn-down slow client re-hydrates
  via `GET /alerts` (§3·j), so coalescing never loses durable truth.

## Invariants when you touch this

- **`Tick`, `TickConditions` and `TickUpdates` are the only writers of alert state**, all on the one poll-loop thread (never
  concurrently — `PollAsync` calls them sequentially each tick); the controller reads the volatile
  immutable snapshot. `_lastStartAction` is the only cross-thread state (concurrent). Keep it that way —
  don't mutate `_firing`/`_resolved`/`_clearSince`/`_breachSince` off the loop, or you reintroduce a lock.
- **Always-on, not subscriber-gated** (like `LeafHealthMonitor`, unlike the metric pumps): `GET /alerts`
  must serve fresh truth regardless of stream subscribers. With no watchdog provisioned the loop logs once and
  serves an **empty** feed (degrade gracefully — never a 500).
- **Severity is the §3·c subset** (`danger|warn|info`) — no `success` (a firing condition is never a
  success). `resolution.by` is **always `system`** (the server observed the clear, never the client).
- **`anchor.surface`** is a best-effort deep-link hint (`server` for a crash); the frontend always has
  `serverId`/`hostId` to route from if it doesn't recognize the surface. Confirm the surface vocabulary at
  the frontend gate before adding values.

## Auth

`GET /alerts` is `[Authorize(Policy = viewer)]` and the `alerts` topic rides the viewer-gated
`/stream` connection — a core read surface, consistent with `/audit`.
