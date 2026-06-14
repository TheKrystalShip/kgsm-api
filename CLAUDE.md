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
capabilities) and **M1·b** (servers — the kgsm-lib domain+run-state ⋈ monitor metrics join, the
honest `Server` DTO) are `built` & self-validated (`scripts/smoke.sh` 18/18, degrade + happy
path); M2–M8 are `planned`. Trust `PLAN.md`'s per-milestone status, not assumptions.

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
it asserts every M0/M1 contract over `curl` (18/18). It runs two phases: Phase A degrade (no
monitor, live kgsm) and Phase B an **embedded stub monitor** (a unix socket serving a canned
`Snapshot`) that makes the host happy path + the M1·b servers-join present-branch deterministic
with no external monitor. Knobs: `SMOKE_PORT`, `SMOKE_SKIP_BUILD=1`, `SMOKE_DB`, `SMOKE_KGSM_PATH`
(the engine on another host), `SMOKE_MONITOR_SOCKET` (a live monitor in Phase A).
**Runtime config lives in `appsettings.json`** — the documented schema + defaults
for every `KGSM_API_*` key (host identity, the **kgsm engine path/socket**, the
monitor/watchdog/assistant endpoints, bind `KGSM_API_URLS`, `KGSM_API_DB`,
`KGSM_API_CORS_ORIGINS`). Each is **overridable by an env var
of the same name** (env wins — that's how the systemd unit and smoke configure a host); a
blank leaf endpoint reports its capability `absent`. There is **no test project yet**
(`tests/Api.Tests/` is planned — see `PLAN.md §7`); smoke is the current gate.

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
  `GET /servers` (`IInstanceService.GetAll` + `GetAllStatuses(fast:true)`). kgsm-lib is **base,
  not a leaf**: provisioned-by-default at `KGSM_API_KGSM_PATH` (`/usr/bin/kgsm`); an empty path is
  a surfaced misconfiguration (empty `/servers` + a one-time log), not a §4·b capability. The
  process-based `IInstanceService` is transient → resolved per-request from the provider; the kgsm
  event socket (`KGSM_API_KGSM_SOCKET`) is only a registration formality until the M5 event consumer.
- **Monitor** (host + per-instance metrics) → **scrape its unix socket**
  (`/run/kgsm-monitor.sock`, `GET /metrics`) directly — that's the monitor's neutral public
  output; reuse the watchdog client's `SocketsHttpHandler.ConnectCallback` pattern (done in
  `Services/Leaves/MonitorClient.cs`, M1·a). The snapshot is deserialized into the **shared
  `TheKrystalShip.KGSM.Monitor.Contracts`** package (the `Snapshot` graph + its source-gen
  camelCase JSON context), built in the kgsm-monitor repo — so producer and consumer share
  ONE build-time contract. **Never re-declare a local copy of the monitor DTOs.** Drift rule:
  any contract change bumps the package `Version` AND this project's `<PackageReference>` —
  a same-version repack is served stale from the NuGet cache (`id+version` keyed).
- **Assistant** → the typed **`Services/Leaves/AssistantClient.cs`** (a dedicated
  `HttpClient` subclass, not raw HTTP in the aggregator). M1·a uses it only for a liveness
  `ProbeAsync` (the §4·b capability); it is the home the tool catalog, capability discovery,
  and the **HTTP/SSE** turn relay (M7) grow into. Probe self-bounds via a linked token — leave
  the client's `Timeout` at default so future slower calls aren't capped by the probe budget.

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
  `UseStatusCodePages` (404, and 401/403 once M4 lands). `/healthz` is **ours** (ops), not a
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
- **Trust window:** M3 (commands, which mutate) lands before M4 (auth). Acceptable **only**
  on a trusted, non-public network until M4 — see `PLAN.md` M3.
- **`SuppressMapClientErrors=true`** (Startup): `[ApiController]` would otherwise turn a
  controller `NotFound()`/`BadRequest()` into RFC-9110 ProblemDetails. We suppress it so 4xx
  flow through `UseStatusCodePages` → the `{error}` envelope (one error shape everywhere).
  ⚠ When request bodies arrive (M3/M8), model-validation `400`s also bypass ProblemDetails
  and must be routed through the envelope — don't let that surprise you.
- Nothing here is committed unless the user asked; commit/push only on explicit request.
