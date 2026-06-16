# CLAUDE.md — Services/Commands/

The first write path (M3) — `POST /api/v1/servers/{id}/commands { verb }` → **gate → `202` + job →
`jobs` WS (`job.patch`) → verify (`server.patch` on settle)**. Built; the contract is frozen in
`PLAN.md §6` (command/job row) + `§8` (M3 log). This file is the local "what you must not break."

## Locked decisions (do not relitigate)

- **Closed verb set: `start`·`stop`·`restart`·`open_ports`** (`update` deferred — long-running +
  version-changing). Verbs are server-defined in `CommandVerb`; a client/model can't invent one. Add new
  verbs there. **`open_ports` (M6·b)** is intent-only — it carries **NO client port list**; the runner
  derives the target from the instance's own `Instance.Ports` (a client list would let the browser open
  anything). It is **always admissible** (no run-state no-op — declarative/idempotent) and shares the
  one-in-flight slot.
- **`CommandGate` is pure and minimal.** It rejects ONLY the obvious no-ops against the *real observed*
  status (start-when-running / stop-when-stopped). **The engine is the single authority** on what a verb
  does — a subtler-but-impossible transition runs and surfaces as the job's `failed` + kgsm's real error.
  `unknown` status never blocks. **Never fabricate admissibility kgsm doesn't enforce.**
- **`job.state` is the JOB's own lifecycle** (`queued→running→succeeded|failed`), NOT the server's
  status. The affected server's status rides the `servers` topic via `server.patch`; the client derives
  the optimistic display from the verb. (Frozen §6 divergence from the §5·d server-shaped example.)
- **Jobs are in-memory.** `JobRegistry` holds state; jobs themselves are **not** persisted (no jobs table).
  Don't add one.
- **Provenance stamping, NOT an audit write (M5).** The command path stamps `actor`(bearer identity)+
  `origin`(caller-declared surface) onto `ILifecycleService.Start/Stop/Restart(serverId, actor, origin)`
  (kgsm-lib 1.8.0) so the resulting kgsm event carries who/through-what. The **audit row is written by the
  M5 event consumer from that event echo** — the command path does **NOT** write one (kgsm owns `server.*`
  → no double-write). `CommandRunner.Start(job, actor, origin)` threads them; `ServersController` derives
  `actor` from the JWT (`AuditPrincipal`) and validates `origin` (`ui|assistant|discord|api`, default `api`;
  `system` rejected). Don't add an `AuditService` call here **for lifecycle verbs**.
- **`open_ports` is the audit EXCEPTION — a DIRECT write (M6·b).** It goes through `IFirewallService`
  (`EnsureOpenAsync`), which runs no kgsm command and emits **no event**, so there is no echo to read — the
  `auth.*` case. The runner therefore writes the `network.ports.open` row **directly** via `AuditService`
  (`AuditMapping.FromPortsOpenedCommand`), but **only on a real change (`Applied` or `AppliedInactive`)** — a
  `NoOp` succeeds with no row (recording "opened" when nothing changed would fabricate a change; symmetric
  with the CLI echo, which only fires on a confirmed open). `AppliedInactive` (the rule was staged but ufw is
  inactive, Firewall.Contracts 1.1.0) is a real config change, so it IS audited — with `enforced:false`, and
  the summary says "staged" not "opened". This is **not** a double-write: kgsm never echoes an api firewall
  call, and the CLI echo path (`instance_ports_opened`) is disjoint. The `meta` carries `{jobId, ports}` —
  `jobId` is the job↔audit correlation (populatable because the api owns both job + append; the M5 "no jobId"
  limit was the echo path). Don't extend this direct-write to the lifecycle verbs.

## Invariants when you touch this

- **One in-flight command per server**, enforced by `JobRegistry`'s atomic slot claim (`_inFlight`).
  A second concurrent command → `409` + the in-flight job id. The slot releases on a terminal state.
- **`CommandRunner` runs the verb off-request in its own DI scope.** It's a singleton; the verb runs on
  a background `Task` that **creates a fresh `IServiceScope` and resolves the transient/process-based
  `ILifecycleService` THERE**. The request scope is gone by the `202`, so capturing a request-scoped
  service is use-after-dispose. **Only the `Job` (value data) crosses the boundary** — never a scoped service.
- **Always settle in `finally`** — a started job must release its slot and emit a terminal `job.patch`
  even on a throw. On settle, **verify** — lifecycle verbs re-read run-state → `server.patch` (the §5·d
  `command.verified`); `open_ports` re-probes the firewall → `network.patch` on the dedicated
  `servers/{id}/network` topic (so `server.patch` stays the frozen M1·b `Server`). Both reuse the shared
  aggregator build path, so the patch is byte-identical to the matching REST detail field.
- **kgsm-lib only.** Lifecycle goes through `ILifecycleService` (native→watchdog, container→Docker); the
  `open_ports` firewall call goes through `IFirewallService` (M6·b). **Never** shell `kgsm.sh` or open the
  watchdog/firewall socket directly (the ecosystem chokepoint invariant). `IFirewallService` is a
  conditionally-registered singleton (firewall opt-in) — resolve it from the job's scope and degrade
  honestly (`"firewall authority not provisioned"`) when absent, never throw a missing-dependency.

## Auth (M4·a)

The `POST .../commands` action is `[Authorize(Policy = operator)]` — mutations require operator or admin;
viewers reading `/servers` can't issue one. The gate's state-guards are orthogonal to permissions.

## Real-lifecycle caveat

Accurate verify status + reliable stop depend on **`kgsm-watchdog` being up** — without it kgsm
direct-spawns and native run-state tracking is unreliable (PLAN §8 M3 env finding). The verify path is
correct; it publishes what kgsm reports.
