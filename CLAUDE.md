# CLAUDE.md — kgsm-api

Guidance for Claude Code working in **kgsm-api**. Read this, then `PLAN.md` (the staged
roadmap and the authority for what's built vs planned).

## What this is

`kgsm-api` is the **per-host KGSM Control Panel API** — the aggregating web API that the
React SPA (and other surfaces) talk to. One deployable unit = **one host** = `kgsm` +
its leaves + this API. The API aggregates **only its own host's** leaves; cross-host
"fleet" rollup is done **client-side** by the SPA (no `/fleet` endpoint — `architecture.html
§4·a`). It is a **leaf-aggregator**, not part of the engine.

> **This repo is a from-scratch rewrite.** The superseded .NET 9 attempt (it fabricated
> metrics — the sin that got it scrapped, keystone O4) is parked in `legacy/` for
> *harvest only* — **never treat `legacy/` as authoritative or a design reference.** The
> root `tks/CLAUDE.md` still tags kgsm-api "superseded"; that refers to `legacy/`. The
> live project is `src/Api/`, built per `PLAN.md`.

**Status:** M0 (skeleton + runtime/stack decision), **M1·a** (hosts — monitor scrape + §4·b
capabilities), **M1·b** (servers — the kgsm-lib domain+run-state ⋈ monitor metrics join, the
honest `Server` DTO), **M2** (realtime — the `GET /api/v1/stream` WebSocket + the always-on
leaf-health capability model), **M3** (commands — the first write path: `POST /servers/{id}/commands`
→ gate → `202` + job → `jobs` WS → verify; verbs `start`/`stop`/`restart`, `update` deferred) and now
**M4·a** (auth — Discord per-host, Model A; the credential-independent half: stateless JWT bearer +
hierarchical viewer/operator/admin tier policies + `[Authorize]` on every prior endpoint) are
`built` & self-validated (`scripts/smoke.sh` **31/31** + **tests/Api.Tests 30/30**); **M4·b** (the live
Discord OAuth round-trip) and M5–M8 are `planned`. **Auth is ON by default** — `KGSM_API_AUTH_DISABLED=1`
is the explicit, loudly-logged dev escape hatch (synthetic admin; the pre-M4 open trust window). Trust
`PLAN.md`'s per-milestone status, not assumptions.

## Read first (sources of truth)

- **`PLAN.md`** — the milestone roadmap (M0…v1.0), principles, the cross-team contract
  registry, project layout, and the validation log. The authority for *this backend*.
- **`../architecture.html`** — the **frontend team's** external-surface spec (v0.3): REST
  `/api/v1`, per-host WebSocket, assistant SSE, auth Model A, the §6 conventions. The
  authority for *the wire contracts*. Freeze contracts **from this doc**, never invent them.
- **`../system-architecture.md`** — the ecosystem keystone (topology, invariants, the
  open-decision ledger). The API is its `web-API aggregator`.
- **`docs/m0-aot-spike-findings.md`** — why the runtime/stack is what it is (below).

## Commands

```bash
dotnet build kgsm-api.slnx                 # build (Debug)
dotnet run --project src/Api/Api.csproj    # run locally (binds KGSM_API_URLS, default :8080)
scripts/smoke.sh                           # build Release + run the HTTP contract checks (the "mock frontend")
# self-contained deploy artifact (per-host drop-in, no runtime install):
dotnet publish src/Api/Api.csproj -c Release -r linux-x64 --self-contained -p:PublishReadyToRun=true
```

`scripts/smoke.sh` is the **stand-in for the frontend** until the SPA can reach a host —
it asserts every M0/M1/M2/M3 contract (and the M4·a no-token sweep) — **31/31**. The M0–M3 checks
run under `KGSM_API_AUTH_DISABLED=1` (the escape hatch — synthetic admin) so they exercise the domain
contracts unchanged; a dedicated **auth-ENABLED** instance then proves the no-token sweep (every
protected endpoint `401`s with the frozen envelope, `/health`+`/api/v1` stay open, the login endpoint
`503`s until Discord is configured). The 3 M3 checks prove the command gate/rejection
contract (`400`/`404`/`409`) **without mutation** — the gate rejects before a verb runs. The write
happy path (the stub smoke can't reach it) was **live-validated on the trusted host** (2026-06-15):
`202`+job, `job.patch` `running→succeeded`, verify `server.patch`, and the in-flight `409` guard under
6 concurrent POSTs (1×202 / 5×409). NB real native lifecycle needs `kgsm-watchdog` up — without it,
kgsm direct-spawns an orphan and run-state tracking is unreliable (PLAN §8).
It runs two phases: Phase A degrade (no monitor,
live kgsm) and Phase B an **embedded stub monitor** (a unix socket serving a canned `Snapshot`)
that makes the host happy path + the M1·b servers-join present-branch deterministic with no
external monitor. **M2** is covered by an embedded **stdlib RFC6455 WebSocket client** (no
`websocat`/`wscat`/`websockets` dependency) that subscribes, reads honest ticks, and — killing
**then restarting** the stub monitor mid-stream — proves the degrade→recover capability lifecycle
(down flip + tick silence, then operational flip + ticks resume, `provisioned:true` throughout).
Knobs: `SMOKE_PORT`, `SMOKE_SKIP_BUILD=1`, `SMOKE_DB`, `SMOKE_KGSM_PATH` (the engine on another
host), `SMOKE_MONITOR_SOCKET` (a live monitor in Phase A).
**Runtime config lives in `appsettings.json`** — the documented schema + defaults
for every `KGSM_API_*` key (host identity, the **kgsm engine path/socket**, the
monitor/watchdog/assistant endpoints, bind `KGSM_API_URLS`, `KGSM_API_DB`,
`KGSM_API_CORS_ORIGINS`, and the **M4·a auth keys** — `KGSM_API_AUTH_DISABLED`,
`KGSM_API_AUTH_SIGNING_KEY`, the `KGSM_API_AUTH_DISCORD_*` app/bot/guild, the
`KGSM_API_AUTH_ROLE_*` role→tier map). Each is **overridable by an env var
of the same name** (env wins — that's how the systemd unit and smoke configure a host); a
blank leaf endpoint reports its capability `absent`. The Discord app/guild/bot-token/role ids are
**shared external config** (the same values the host's Discord bot uses) — configuration, not a
process dependency on kgsm-bot (keystone §4). **`tests/Api.Tests/`** (xUnit + `WebApplicationFactory`,
the Discord seam faked) stands up at M4·a — `dotnet test kgsm-api.slnx`; it owns the 401/403/tier
matrix + the callback/refresh/session flow, with smoke covering the HTTP contract surface.

## The stack decision — do NOT undo it

**Standard JIT, MVC controllers + EF Core (SQLite). NOT Native AOT** — even though the
rest of the ecosystem (kgsm-lib/monitor/watchdog) is AOT. This was decided deliberately
at M0: a spike proved AOT *viable*, then JIT was chosen anyway for long-term
maintainability, because controllers and EF Core are both AOT-incompatible (verified:
"MVC does not support native AOT"; "EF Core isn't fully compatible with NativeAOT").

- **Do not suggest making the API AOT "for consistency"** — it was considered and rejected.
  The API is the one component where this is sound: it's *not embedded* in an AOT host
  (unlike kgsm-lib) and is the broadest, highest-churn surface.
- Ecosystem correctness is intact: **kgsm-lib stays AOT-safe and is consumed unchanged**
  (AOT code runs fine under JIT). Reflection-based STJ, EF migrations, the conventional
  stack — all fair game here.
- Structure is the classic **`Program` + `Startup`** (generic host + `UseStartup<Startup>`),
  not top-level statements — DI in `ConfigureServices`, pipeline in `Configure`.

## How it's wired (the consumption model)

The API **aggregates leaves; no leaf depends on the API** (keystone §4). Each input has
exactly one correct access path:

- **Engine** (instances, run-state, config, lifecycle commands) → **only via `kgsm-lib`**
  (`TheKrystalShip.KGSM`, the single C#↔engine chokepoint; it reaches the watchdog via
  `IWatchdogClient`). **Never shell out to `kgsm.sh` or open the watchdog socket directly.**
  Added in M1 (local feed: `/home/heisen/local-nuget`, currently 1.6.0). Wired at **M1·b** for
  `GET /servers` (`IInstanceService.GetAll` + `GetAllStatuses(fast:true)`) and at **M3** for the write
  path (`ILifecycleService.Start/Stop/Restart`, run off-request by the `CommandRunner` in its own DI
  scope — the verb routes native→watchdog, container→Docker inside the engine). kgsm-lib is **base,
  not a leaf**: provisioned-by-default at `KGSM_API_KGSM_PATH` (`/usr/bin/kgsm`); an empty path is
  a surfaced misconfiguration (empty `/servers` + a one-time log), not a §4·b capability. The
  process-based `IInstanceService` is transient → resolved per-request from the provider; the kgsm
  event socket (`KGSM_API_KGSM_SOCKET`) is only a registration formality until the M5 event consumer.
- **Monitor** (host + per-instance metrics) → **scrape its unix socket**
  (`/run/kgsm-monitor.sock`, `GET /metrics`) directly — that's the monitor's neutral public
  output; reuse the watchdog client's `SocketsHttpHandler.ConnectCallback` pattern (done in
  `Services/Leaves/MonitorClient.cs`, M1·a). M2 added `CheckHealthAsync` (`GET /health`) as the
  liveness signal, **separate from the data scrape** (a warming monitor is operational with no
  frame yet). The snapshot is deserialized into the **shared
  `TheKrystalShip.KGSM.Monitor.Contracts`** package (the `Snapshot` graph + its source-gen
  camelCase JSON context), built in the kgsm-monitor repo — so producer and consumer share
  ONE build-time contract. **Never re-declare a local copy of the monitor DTOs.** Drift rule:
  any contract change bumps the package `Version` AND this project's `<PackageReference>` —
  a same-version repack is served stale from the NuGet cache (`id+version` keyed).
- **Assistant** → the typed **`Services/Leaves/AssistantClient.cs`** (a dedicated
  `HttpClient` subclass, not raw HTTP in the aggregator). It exposes a liveness `CheckHealthAsync`
  (`GET /health`, M2) for the §4·b capability; it is the home the tool catalog, capability discovery,
  and the **HTTP/SSE** turn relay (M7) grow into. Probe self-bounds via a linked token — leave
  the client's `Timeout` at default so future slower calls aren't capped by the probe budget.

**Leaf health & the capability model (M2).** Capability **availability** is owned by the always-on
**`Services/Leaves/LeafHealthMonitor.cs`**, which polls each *provisioned* leaf's health every ~2s
(monitor + assistant `GET /health`; watchdog `IsReadyAsync` via kgsm-lib — never a direct socket).
It is the **single source** feeding both the REST `GET /hosts` capability block (`HostAggregator`
reads its cached `Current`) and the M2 `hosts/{id}/capabilities` stream (it publishes flips). Two
axes, never conflated: **`provisioned`** (the capability *set*) is fixed at startup from config and
**never flips at runtime** — it is what the frontend negotiates at connect; **`status`** is the live
availability. A leaf failing flips only `status` (operational→down→operational) with
`provisioned:true` — "temporarily unavailable, still there", **never** "lost"; never invent a softer
status nor suppress the down flip. `since` = when *this api* observed the flip.
**Uniform `/health` across the ecosystem (unified 2026-06-15):** every leaf now serves `GET /health`
(`200` ⇒ can provide its capability; else ⇒ unavailable). monitor `/healthz`→`/health`; assistant already
`/health`; watchdog merged `/healthz`+`/ready`→`/health` (readiness; `/ready` kept as a deprecated transition
alias) — reached via kgsm-lib `IsReadyAsync` (the api pins kgsm-lib **1.6.0**, which still hits `/ready`, so
it rides the alias until it adopts **1.7.0**). The api's own ops endpoint is also `/health` now. (PLAN.md §8.)

**Degrade gracefully:** a missing/down leaf removes only its capability (the §4·b
capabilities block makes this first-class), never a 500. The API must run with any subset
of leaves present.

## Invariants — violating these is how the old API died

1. **Never fabricate a metric, status, or alert.** Measured, or explicitly "unknown" —
   never invented (no `Random`, no GC-heap-as-RAM; that scrapped the old one). Honest
   `null`/`unknown` over a plausible default.
2. **Metric-presence ≠ status, status-presence ≠ status.** Run-state comes from kgsm-lib's
   façade (`Reading<InstanceRuntimeStatus>`, which can itself be `unknown`); metrics come
   from the monitor; join them — never infer run-state from whether a metrics row exists.
3. **Freeze contracts FROM `architecture.html`, don't invent them.** The aspirational
   `Server` example there asks for `cpu`(0–100), `ram.max`, `players`, `ip` — none honestly
   sourceable today. The **honest DTO** (M1·b) emits `cpuPctCore` (% of one core, can
   exceed 100), `memBytes`, nullable `io*`, and **omits the unsourceable** — this divergence
   is a deliberate, frontend-negotiated contract, the project's most important conversation.
   Record every frozen shape in `PLAN.md §6`.
4. **Additive-only within `/api/v1`** (path-versioned). Grow into reserved fields, no break.
5. **Persistence is downstream of the stateless engine.** The API persists only its *own*
   operational metadata (sessions M4, append-only audit M5) via EF; the domain is
   live-scraped, never stored. KGSM stays stateless (the watchdog is the lone resident
   exception, and it's engine, not this API).

## Conventions

- **JSON:** camelCase + ISO-8601 UTC **`Z`** timestamps, configured once in `Json/ApiJson.cs`
  and applied to both MVC and HTTP options. Add new `DateTimeOffset` fields and they inherit
  `Z` automatically.
- **Errors:** every non-2xx returns the frozen envelope `{ "error": { "code", "message",
  "details?" } }` (`architecture.html §6`) — via `ApiExceptionHandler` (500s) and
  `UseStatusCodePages` (404, and 401/403 once M4 lands). `/health` is **ours** (ops), not a
  frontend contract.
- **Namespaces** are `TheKrystalShip.Api.*` (ecosystem-wide `TheKrystalShip.*`).
- **Validation model:** each milestone ends at a **frontend gate** — agree the wire shapes
  first (`§6`), build + self-prove (smoke + a live leaf), then the frontend swaps its store
  mock → real. Caution on the wiring; this is the first time frontend + backend + leaves
  merge.

## Gotchas

- **`legacy/`** is the scrapped .NET 9 API — harvest patterns (e.g. log-streaming) but
  treat nothing in it as correct (it fabricates metrics).
- **EF `EnsureCreated` vs migrations:** M0's `_dbcheck` uses `EnsureCreatedAsync` (no
  `__EFMigrationsHistory`). When M5 introduces migrations, start from a clean DB or
  `ef database update` will conflict.
- **Diagnostics endpoints** (`/api/v1/_throw`, `/api/v1/_dbcheck`) are smoke-only probes —
  remove/restrict before any public exposure.
- **Trust window:** M3 (commands, which mutate) lands before M4 (auth) — **CONFIRMED acceptable**
  (user, 2026-06-15) **only** on a trusted, non-public network until M4. The M3 write path is
  unauthenticated by design this milestone; the gate enforces state guards only (permissions at M4).
  See `PLAN.md` M3.
- **`SuppressMapClientErrors=true`** (Startup): `[ApiController]` would otherwise turn a
  controller `NotFound()`/`BadRequest()` into RFC-9110 ProblemDetails. We suppress it so 4xx
  flow through `UseStatusCodePages` → the `{error}` envelope (one error shape everywhere).
  ⚠ When request bodies arrive (M3/M8), model-validation `400`s also bypass ProblemDetails
  and must be routed through the envelope — don't let that surprise you.
- Nothing here is committed unless the user asked; commit/push only on explicit request.
