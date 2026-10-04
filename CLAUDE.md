# CLAUDE.md — kgsm-api

Guidance for Claude Code working in **kgsm-api**. Read this, then `PLAN.md` (the staged roadmap and
the authority for what's built vs planned).

## What this is

`kgsm-api` is the **per-host KGSM Control Panel API** — the aggregating web API that the React SPA (and
other surfaces) talk to. One deployable unit = **one host** = `kgsm` + its leaves + this API. The API
aggregates **only its own host's** leaves; cross-host "fleet" rollup is done **client-side** by the SPA
(no `/fleet` endpoint — `architecture.html §4·a`). It is a **leaf-aggregator**, not part of the
engine. The live project is `src/Api/`; `legacy/` is a scrapped .NET 9 attempt that fabricated
metrics, kept for *harvest only* — **never treat it as authoritative or a design reference.**

**This API signs nobody in.** Every session it accepts is minted by the cluster's auth anchor
(`tks-auth`), which every install runs — a machine on its own is a cluster of one — and is
verified here against the key that anchor publishes; what a caller may do is evaluated per action from
its replica of the cluster's authority on every request. **Auth is ON by default** —
`Api__AuthDisabled=true` is the explicit, loudly-logged dev escape hatch (a synthetic Owner). Detail:
`src/Api/Services/Auth/CLAUDE.md`.

## Read first (sources of truth)

- **`PLAN.md`** — the milestone roadmap, principles, the cross-team contract registry, project layout,
  and the validation log. The authority for *this backend*.
- **`../architecture.html`** — the **frontend team's** external-surface spec: REST `/api/v1`, the
  per-host realtime stream, assistant SSE, the §6 conventions. The authority for *the wire contracts*.
  Freeze contracts **from this doc**, never invent them.
- **`../system-architecture.md`** — the ecosystem keystone. The API is its `web-API aggregator`.
- **`docs/m0-aot-spike-findings.md`** — why the runtime/stack is what it is (below).
- **Directory-local `CLAUDE.md` guides** — the locked decisions for each subsystem, auto-loaded when you
  work in them: `src/Api/Services/Auth/` (accepting the anchor's sessions, the ended-session deny-list,
  authority from the replica), `src/Api/Services/Leaves/` (leaf health and capabilities, the leaf
  clients, leaf configuration and command manifests), `src/Api/Services/Audit/`,
  `src/Api/Services/Alerts/`, `src/Api/Realtime/` (the SSE stream protocol),
  `src/Api/Services/Commands/` (the gate→job→verify write path), `tests/Api.Tests/` (the
  WebApplicationFactory + faked-seam test pattern), `scripts/` (the smoke) and `deploy/`.

## Commands

```bash
dotnet build kgsm-api.slnx                 # build (Debug)
dotnet test  kgsm-api.slnx                 # tests/Api.Tests — the 401/403 access matrix and the rest
dotnet run --project src/Api/Api.csproj    # run locally (binds Api__Urls, default :8080)
scripts/smoke.sh                           # build Release + run the HTTP contract checks (the "mock frontend")
./deploy/deploy.sh                         # build + (re)deploy the live systemd service — see deploy/CLAUDE.md
```

## Configuration

**`src/Api/kgsm-api.settings.json` declares the whole configurable surface** — every key with its
default, under one `Api` section that `ApiSettings` binds 1:1. `ApiOptions.FromSettings` is the one
place any of it is interpreted; nothing reads configuration by string key. An environment variable
**overrides one key** by spelling that key's path with `__` (`Api__DomainPollMs`,
`Logging__LogLevel__Default`), and env wins because it is registered last. A variable naming a key the
file does not declare **binds to nothing**, and the build fails if the settings file and `ApiSettings`
disagree in any direction. The boolean knobs take **`true`/`false` only** — any other spelling is
refused at startup with an error naming the key.

**`deploy/kgsm-api.leaf.json` is generated, not written.** `TheKrystalShip.KGSM.ComponentConfig`
rewrites it on every build from `[ConfigField]` attributes and `<panel>` doc tags on `ApiSettings` — so
edit the settings class, never the JSON, and commit what the build produces. Beside it the same build
writes `deploy/kgsm-api.leaf.actions.json`, the actions this API performs and the engine actions it
requires as its own service account (`src/Api/ApiActionDeclarations.cs`), and fails when an engine call
is neither required nor named by the code making it for a checked person. A settings key nothing
describes fails the build naming it; `AllowedHosts` is declared exempt, because host filtering belongs
to the framework rather than to this API's configuration surface. This API is the one leaf whose
descriptor says `readOnly` — applying a change here means restarting the process serving the request.

## The stack decision — do NOT undo it

**Standard JIT, MVC controllers + EF Core (SQLite). NOT Native AOT** — even though the rest of the
ecosystem (kgsm-lib/monitor/watchdog) is AOT. Controllers and EF Core are both AOT-incompatible, and
long-term maintainability wins here.

- **The API stays JIT — do not propose making it AOT "for consistency".** The API is the one component
  where this is sound: it's *not embedded* in an AOT host (unlike kgsm-lib) and is the broadest,
  highest-churn surface.
- Ecosystem correctness is intact: **kgsm-lib stays AOT-safe and is consumed unchanged** (AOT code runs
  fine under JIT). Reflection-based STJ, the conventional stack — all fair game here.
- Structure is the classic **`Program` + `Startup`** (generic host + `UseStartup<Startup>`), not
  top-level statements — DI in `ConfigureServices`, pipeline in `Configure`.

## How it's wired

- **Engine** (instances, run-state, config, lifecycle commands) → **only via `kgsm-lib`**
  (`TheKrystalShip.KGSM`, the single C#↔engine chokepoint; it reaches the watchdog via
  `IWatchdogClient`, resolved as `ProvisionedWatchdogClient`). **Never shell out to `kgsm.sh` or open
  the watchdog socket directly.** Consumed as a versioned `PackageReference` from the org's GitHub
  Packages feed. It backs `GET /servers` (`IInstanceService.GetAll` + `GetAllStatuses(fast:true)`) and
  the write path (`ILifecycleService.Start/Stop/Restart`, run off-request by the `CommandRunner` —
  `src/Api/Services/Commands/CLAUDE.md`). kgsm-lib is **base, not a leaf**: provisioned-by-default at
  `Api__KgsmPath` (`/usr/bin/kgsm`); an empty path is a surfaced misconfiguration (empty `/servers` + a
  one-time log), not a capability. The process-based `IInstanceService` is transient → resolved
  per-request. The engine's event journal is tailed live for SSE and notifications
  (`src/Api/Services/Audit/CLAUDE.md`).
- **Leaves** (monitor, assistant, speech, watchdog, scheduler, reactor, firewall, bot) →
  `src/Api/Services/Leaves/CLAUDE.md`. A missing/down leaf removes only its capability, never a 500.

## Invariants — non-negotiable

1. **Never fabricate a metric, status, or alert.** Measured, or explicitly "unknown" — never invented
   (no `Random`, no GC-heap-as-RAM). Honest `null`/`unknown` over a plausible default.
2. **Metric-presence ≠ status, status-presence ≠ status.** Run-state comes from kgsm-lib's façade
   (`Reading<InstanceRuntimeStatus>`, which can itself be `unknown`); metrics come from the monitor;
   join them — never infer run-state from whether a metrics row exists.
3. **Freeze contracts FROM `architecture.html`, don't invent them.** The aspirational `Server` example
   there asks for `cpu`(0–100), `ram.max`, `players`, `ip` — none honestly sourceable. The **honest
   DTO** emits `cpuPctCore` (% of one core, can exceed 100), `memBytes`, nullable `io*`, and **omits
   the unsourceable** — this divergence is a deliberate, frontend-negotiated contract. Record every
   frozen shape in `PLAN.md §6`.
4. **Additive-only within `/api/v1`** (path-versioned). Grow into reserved fields, no break.
5. **Persistence is downstream of the stateless engine.** The API persists only its *own* operational
   metadata — the append-only **audit log** and the **ended-session list** (see `Services/Auth/`) — via
   EF; the domain is live-scraped, never stored. **The audit is event-sourced, single-writer, no
   double-write:** kgsm owns `server.*`/`backup.*`, so the API records the engine's event **echo** — it
   never writes a row when it *issues* a command; the command path only **stamps** `actor`+`origin`
   onto the engine call so they ride the event. **Never** add a second writer for an action kgsm
   already emits, and never derive `origin` from the actor. See `Services/Audit/CLAUDE.md`.

## Conventions

- **JSON:** camelCase + ISO-8601 UTC **`Z`** timestamps, configured once in `src/Api.Contracts/ApiJson.cs` and
  applied to both MVC and HTTP options. New `DateTimeOffset` fields inherit `Z` automatically.
- **Errors:** every non-2xx returns the frozen envelope `{ "error": { "code", "message", "details?" } }`
  (`architecture.html §6`) — via `ApiExceptionHandler` (500s) and `UseStatusCodePages` (404, 401, 403).
  `/health` is **ours** (ops), not a frontend contract.
- **Namespaces** are `TheKrystalShip.Api.*`.
- **Versioning (two axes — don't conflate):** the **route version** is the `/api/v1` path segment
  (`ApiInfo.ApiVersion`, surfaced as `version`/`panelVersion`) — additive-only, changes only on a
  breaking generation. The **build version** is `<Version>` in `Api.csproj` **+ the git SHA
  auto-stamped by the `SetSourceRevisionId` target** (`<version>+<sha>`) — surfaced as `build` on
  `GET /api/v1` and `identity.build` on the Host DTO. The SHA degrades to absent (never fabricated)
  outside a git checkout. **Full reference: `README.md` §Versioning.**
- **Logging:** the ecosystem convention (`../logging-convention.md`). The notification-webhook
  `HttpClient` keeps `.RemoveAllLoggers()` (Startup) — that's load-bearing secret-redaction, never drop
  it.
- **Validation model:** agree the wire shapes first (`PLAN.md §6`), build + self-prove (smoke + a live
  leaf), then the frontend swaps to the real endpoint.

## Version tracking

- **Version source:** `<Version>` in `src/Api/Api.csproj`; the build appends the short git SHA to
  `AssemblyInformationalVersion`.
- **Packaging reads it via `deploy/version.sh`** — `./deploy/version.sh` prints the declared version,
  `--pkgver` prints the pacman-safe form. A package never restates a version number; it asks for one.
- Bump the version whenever you make a user-facing change (patch for fixes, minor for features, major
  for breaking changes), with a `CHANGELOG.md` entry under `## [Unreleased]`.

## Gotchas

- **EF `EnsureCreated`, NOT migrations.** The schema is created via `EnsureCreatedAsync` (no
  `__EFMigrationsHistory`), and a schema change means **wiping the dev DB**, not adding a migration.
  `EnsureCreated` **no-ops on an existing DB** — so after any entity change, delete the DB file or the
  new column/table silently won't exist and queries 500 at runtime, not build. Don't introduce
  `Migrations/` without re-deciding this.
- **`SuppressMapClientErrors=true`** (Startup): `[ApiController]` would otherwise turn a controller
  `NotFound()`/`BadRequest()` into RFC-9110 ProblemDetails; suppressing it sends 4xx through
  `UseStatusCodePages` → the `{error}` envelope. It only covers *result*-based 4xx — a
  model-binding/validation `400` (malformed JSON, or a body field of the wrong type) is rejected by
  `[ApiController]` **before the action runs**, so Startup's `ConfigureApiBehaviorOptions` sets an
  `InvalidModelStateResponseFactory` that returns the `{error:{code:"bad_request"…}}` envelope
  (regression-tested). **Don't remove it** — it keeps "every non-2xx is the envelope" true for any
  typed request body.
