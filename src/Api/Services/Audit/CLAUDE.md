# CLAUDE.md — Services/Audit/

The append-only **audit log** (M5, architecture.html §3·d) — persistence *downstream* of the stateless
engine (keystone O3). `GET /api/v1/audit` (keyset) + the `audit` WS topic (`audit.append`). Built; the
contract is frozen in `PLAN.md §6` (audit row) + `§8` (M5 log). This file is the local "what you must not break."

## Locked decisions (do not relitigate)

- **Event-sourced, single writer, NO double-write.** kgsm **owns** `server.*`/`backup.*`. `AuditService`
  is the one writer; engine actions land via `KgsmAuditConsumer` (the kgsm-lib `IEventService` echo) —
  the API **never** writes an audit row when it *issues* a command. The command path only **stamps**
  `actor`+`origin` onto `ILifecycleService.*(serverId, actor, origin)` (kgsm-lib 1.8.0) so they ride the
  event. `auth.*` has no kgsm event → written directly (no double-write risk). **Never add a second
  writer for an action kgsm already emits** — you can't dedup echoes, which is the whole reason provenance
  is stamped instead.
- **actor and origin are independent axes.** `actor` = who (identity); `origin` = the surface. **Never
  derive origin from actor.** A missing/unknown origin is **`null`**, never a fabricated surface (the
  never-fabricate invariant). The command path's origin is **caller-declared** (`ui|assistant|discord|api`,
  default `api`; `system` is reserved for autonomous engine actions and rejected at the controller).
- **Append-only & immutable.** Rows are never updated or deleted; a correction is a *new* row. Don't add
  an update/delete path. `EnsureCreated`, **not an EF migration** (dev authority — wipe the DB on a schema
  change; see the api `CLAUDE.md` gotcha).
- **Closed `action` vocabulary** (`Contracts/AuditAction`). Clients/the model can't invent one. Add an
  action only when its producer has landed, never speculatively. **M6·0 added** `server.crash` (watchdog
  `instance_crashed`→warn / `instance_failed`→danger, both `system`-stamped) and `network.ports.open`/
  `network.ports.close` (the CLI-path `instance_ports_opened`/`_closed` echoes). `network.ports.close` is a
  deliberate **server-side additive** action beyond the doc's `ports.open`-only `network` set — it is now
  honestly sourceable and keeps the trail symmetric (a standalone `files firewall disable` would otherwise
  leave an opened-never-closed gap); the frontend accepts unknown actions forward-compat. `config.*`/`player.*`/
  `host.*`/… stay deferred. The api-issued `open_ports` command writes `network.ports.open` **directly** (M6·b)
  — kgsm runs nothing, so there is no echo to read (the `auth.*` case); there is no api close command (§3·g is
  open-only), so `network.ports.close` is cleanly CLI-echo-only. The per-event mapping policy lives in the
  **pure** `AuditMapping.From{Crash,Failed,PortsOpened,PortsClosed}Event` mappers, unit-tested without a socket.
- **M6·b: `open_ports` writes `network.ports.open` DIRECTLY** (`AuditMapping.FromPortsOpenedCommand`, called by
  `CommandRunner`) — the `auth.*` case: it goes through `IFirewallService`, which emits no event, so there is no
  echo and no double-write (the CLI echo path above is disjoint). Written **only on a real change (`Applied` or
  `AppliedInactive`)**, not a `NoOp` (recording "opened" when nothing changed fabricates a change; symmetric with
  the CLI echo). `AppliedInactive` (rule staged, ufw inactive — Firewall.Contracts 1.1.0) audits with
  `enforced:false` and a "staged" summary, distinct from an enforced "opened". Its `meta` carries **`jobId`**
  (the job↔audit correlation — see the next bullet) plus the opened `ports`.
- **`origin` nullable** is a recorded §6 divergence. **`meta.jobId` was the OTHER divergence ("not populatable")
  — but that was the event-ECHO path only.** The M6·b `open_ports` DIRECT write CAN populate it (the api owns
  both the job and the append), so its row carries `meta.jobId` for the alert↔audit `resolution.actionId` bridge
  (M6·a). The echo path still can't (no id round-trips the stateless engine) — keep that limit for `server.*`.

## Invariants when you touch this

- **Serialized writes.** `AuditService` holds a `SemaphoreSlim` write gate (SQLite is single-writer) and
  its **own DI scope per write** (writes arrive off the request path — the event listener). Don't capture
  a request-scoped `AppDbContext`. Reads (`AuditController`) use the request scope directly via `AuditQueries`.
- **Keyset pagination on `rowid`**, newest first (`RowId < cursor` `DESC`). Never offset pagination (it
  skips/repeats as the head grows). `nextCursor` only when the page came back full.
- **WS coalesce key = the unique event id** (`StreamProtocol.AuditEntityKey`), NOT a static `"audit"` key:
  audit appends are distinct facts and must never supersede one another in a slow client's queue. (Contrast
  the metric/status patches, which *are* supersede-by-latest.)
- **Actor parse fidelity** (`AuditMapping.ParseActor`): `provider:name` → `{kind,name,provider}`, kind
  derived from provider (`discord`→user, `api`→token, `system`→system); bare string = the OS-user fallback;
  unknown provider keeps the name but leaves provider `null` (never coerce). The load-bearing test is the
  **round-trip** (`discord:haru` → `{user,haru,discord}`) — keep it green when you touch the parser.
- **kgsm-lib only / the listener owns its socket.** Events come through `IEventService`, never a raw socket.
  `UnixSocketClient` **binds + deletes** its path, so `KGSM_API_KGSM_SOCKET` must be a **dedicated** path
  (in kgsm's `config_event_socket_filenames`), never one another consumer owns.

## Degrade gracefully (don't crash startup)

`KgsmAuditConsumer.StartAsync` always `EnsureCreated`s (so `GET /audit` + auth writes work with no engine),
then wires events **only if** the engine is provisioned and `IEventService` resolves; a bad/absent socket is
logged and skipped (the bind faults kgsm-lib's own fire-and-forget task, never throws here). An auth audit
write is best-effort — a failed write must never break login. **Honest boundary:** events emitted while the
API isn't listening are **not** audited (stateless engine, no backfill) — state it, don't try to backfill.

## Auth (M4·a)

`GET /audit` is `[Authorize(Policy = viewer)]` — a core read surface ("every 'what happened' view reads
here"). The `audit` WS topic rides the same viewer-gated `/stream` socket. Trivially bump to operator if the
feed is later deemed sensitive — but viewer is the deliberate default.
