# CLAUDE.md — Services/Commands/

The first write path (M3) — `POST /api/v1/servers/{id}/commands { verb }` → **gate → `202` + job →
`jobs` WS (`job.patch`) → verify (`server.patch` on settle)**. Built; the contract is frozen in
`PLAN.md §6` (command/job row) + `§8` (M3 log). This file is the local "what you must not break."

## Locked decisions (do not relitigate)

- **Closed verb set: `start`·`stop`·`restart`** (`update` deferred — long-running + version-changing).
  Verbs are server-defined in `CommandVerb`; a client/model can't invent one. Add new verbs there.
- **`CommandGate` is pure and minimal.** It rejects ONLY the obvious no-ops against the *real observed*
  status (start-when-running / stop-when-stopped). **The engine is the single authority** on what a verb
  does — a subtler-but-impossible transition runs and surfaces as the job's `failed` + kgsm's real error.
  `unknown` status never blocks. **Never fabricate admissibility kgsm doesn't enforce.**
- **`job.state` is the JOB's own lifecycle** (`queued→running→succeeded|failed`), NOT the server's
  status. The affected server's status rides the `servers` topic via `server.patch`; the client derives
  the optimistic display from the verb. (Frozen §6 divergence from the §5·d server-shaped example.)
- **Jobs are in-memory.** `JobRegistry` holds state; SQLite + the audit write land at **M5**. Don't add
  a jobs table now.

## Invariants when you touch this

- **One in-flight command per server**, enforced by `JobRegistry`'s atomic slot claim (`_inFlight`).
  A second concurrent command → `409` + the in-flight job id. The slot releases on a terminal state.
- **`CommandRunner` runs the verb off-request in its own DI scope.** It's a singleton; the verb runs on
  a background `Task` that **creates a fresh `IServiceScope` and resolves the transient/process-based
  `ILifecycleService` THERE**. The request scope is gone by the `202`, so capturing a request-scoped
  service is use-after-dispose. **Only the `Job` (value data) crosses the boundary** — never a scoped service.
- **Always settle in `finally`** — a started job must release its slot and emit a terminal `job.patch`
  even on a throw. On settle, re-read run-state and publish a `server.patch` (the §5·d `command.verified`).
- **kgsm-lib only.** Lifecycle goes through `ILifecycleService` (native→watchdog, container→Docker) —
  never shell `kgsm.sh` or open the watchdog socket directly (the ecosystem chokepoint invariant).

## Auth (M4·a)

The `POST .../commands` action is `[Authorize(Policy = operator)]` — mutations require operator or admin;
viewers reading `/servers` can't issue one. The gate's state-guards are orthogonal to permissions.

## Real-lifecycle caveat

Accurate verify status + reliable stop depend on **`kgsm-watchdog` being up** — without it kgsm
direct-spawns and native run-state tracking is unreliable (PLAN §8 M3 env finding). The verify path is
correct; it publishes what kgsm reports.
