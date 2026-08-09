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
> live project is `src/Api/`, built per `PLAN.md`.

**Status:** `PLAN.md` is the authority for what's built vs planned, per milestone.
**Auth is ON by default** — `Api__AuthDisabled=true` is the explicit, loudly-logged
dev escape hatch (synthetic admin). Trust `PLAN.md`'s per-milestone status, not assumptions.

## Read first (sources of truth)

- **`PLAN.md`** — the milestone roadmap (M0…v1.0), principles, the cross-team contract
  registry, project layout, and the validation log. The authority for *this backend*.
- **`../architecture.html`** — the **frontend team's** external-surface spec (v0.3): REST
  `/api/v1`, per-host WebSocket, assistant SSE, auth Model A, the §6 conventions. The
  authority for *the wire contracts*. Freeze contracts **from this doc**, never invent them.
- **`../system-architecture.md`** — the ecosystem keystone (topology, invariants, the
  open-decision ledger). The API is its `web-API aggregator`.
- **`docs/m0-aot-spike-findings.md`** — why the runtime/stack is what it is (below).
- **Directory-local `CLAUDE.md` guides** — the locked decisions + "what you must not break"
  for the subsystems with the densest invariants (auto-loaded when you work in them):
  `src/Api/Services/Auth/` (the auth seam, stateless JWT, secure-by-default tiers),
  `src/Api/Realtime/` (the WS protocol), `src/Api/Services/Commands/` (the gate→job→verify write
  path), and `tests/Api.Tests/` (the WebApplicationFactory + faked-seam test pattern).

## Commands

```bash
dotnet build kgsm-api.slnx                 # build (Debug)
dotnet run --project src/Api/Api.csproj    # run locally (binds Api__Urls, default :8080)
scripts/smoke.sh                           # build Release + run the HTTP contract checks (the "mock frontend")
# self-contained deploy artifact (per-host drop-in, no runtime install):
dotnet publish src/Api/Api.csproj -c Release -r linux-x64 --self-contained -p:PublishReadyToRun=true
./deploy/deploy.sh                         # build + (re)deploy the live systemd service in one go (see below)
```

### Deploying / redeploying the live service

Two scripts in `deploy/` — the same pattern every `kgsm-*` repo uses, vendored here so a
standalone clone deploys with nothing else checked out:

```bash
./deploy/setup.sh    # ONCE per host — asks for sudo; provisions and verifies the headless grant
./deploy/deploy.sh   # every deploy — NO sudo, NO prompts
```

**`deploy.sh` needs no privilege at all.** `setup.sh` chowns the install prefix to you (so
installing is a plain file write) and puts the real unit in a **user-owned** directory that
`/etc/systemd/system/` symlinks to (so a unit change is also a plain file write); the only
privileged operations left are the `systemctl` verbs, which go through a polkit rule scoped to
this project's units. If some *other* operation seems to need root, stop and ask — don't
reintroduce `sudo` into `deploy.sh`.

**To (re)deploy the API, run `./deploy/deploy.sh` — do NOT run the individual publish/`systemctl`
steps by hand.** It publishes as the invoking (service-owning) user, bundles the SPA, refreshes the
unit only if it changed, stops the unit, `rsync`s the binary tree into `/opt/kgsm-api`, starts it,
and verifies with a real `HTTP 200` from `/health` (it does not claim success on the launch exit
code alone). The health URL is **resolved from the configured `Api__Urls`**, not hardcoded — on
this host that is loopback `:8097`, while the unit's built-in default is `:8080`. Idempotent; the env
file (`/etc/kgsm-api/kgsm-api.env`) and DB (`/var/lib/kgsm-api`) live outside `/opt` and are never
touched. It opens with a `require_setup` assertion that fails **before building** — with *"run
`deploy/setup.sh`"* — when the host is not provisioned. `deploy/deploy-common.sh` holds the paths,
unit names and helpers both scripts share, so the two can never disagree.

`setup.sh` owns everything privileged: it chowns `/opt/kgsm-api` to you, seeds the env file, puts the
real unit in `/etc/kgsm-api/systemd/` with `/etc/systemd/system/kgsm-api.service` symlinked to it,
installs the scoped deploy polkit grant, enables the unit, and verifies the grant works
unprivileged. **It also wires the runtime leaf-config feature** (the Services panel) via
`deploy/setup-leaf-config.sh`: a per-leaf systemd drop-in (layering an API-owned override env file in
`/var/lib/kgsm-api/leaf-overrides/`) plus a **scoped polkit rule** letting the service user
`systemctl restart` **only** the leaves this host can deliver a config change to
(monitor/watchdog/assistant/firewall/scheduler/bot — kept in lockstep with the leaf→unit map in that
script, which is a superset of the leaves the API connects at runtime). Restart is the *only* privileged op
there; the API renders override files unprivileged. It works under `NoNewPrivileges=true` (restart is
a polkit-authorized D-Bus call to PID 1, not an in-process escalation). Full reference + verify/undo:
`deploy/leaf-config/README.md`.

**What is configurable comes from the leaves, not from here.** Each leaf ships a config descriptor its
own `deploy.sh` installs into `/var/lib/kgsm/leaves/` (`Api__LeafDescriptorDir`), declaring its
full surface — every key, its type, bounds, coded default and `risk`. `LeafDescriptorStore` **scans that
directory**, so a leaf that joins the ecosystem later becomes configurable, and appears on the Services
board, with no rebuild here. `LeafConfigManifest` is the built-in fallback for a leaf that has not
shipped a descriptor yet, not the authority. Format: `../leaf-config-descriptor.md`.

**What a leaf answers to comes from the leaf too.** A leaf that takes typed commands ships a manifest
into `commands/` **below** the descriptor directory — one level down because the descriptor scan globs
`*.json` at the top and would read it as a malformed descriptor. `LeafCommandStore` scans that
subdirectory and `GET /hosts/{id}/services/{leaf}/commands` serves it **verbatim**: the API holds no
idea what any command does, and passes each command's gate through without restating it, because it
cannot verify a check it does not implement. The catalog is keyed by that gate, so a leaf whose
commands need different tiers says so and the panel prints it. A leaf that ships no manifest is a
**404**, not an empty list — most take no commands, and that is a different statement. Read-only
reference material, so it sits at operator with the rest of `ServicesController`. **One schema
version is understood** (`LeafCommandManifest.SupportedSchemaVersion`); anything else is skipped
whole and logged once, never half-read. Format: `../leaf-command-manifest.md`.

Two consequences worth knowing before touching this code:

- **Readable and editable are separate.** A descriptor makes a leaf's config visible with full
  provenance; editing also needs the leaf's override drop-in to exist on this host
  (`Api__LeafDropInDir`), because without it a write renders a file nothing reads. `GET` reports
  `editable:false` with the reason; `PUT` is a **409**, not a 400 — the request is fine, the host is not
  wired.
- **`applied_unreachable` is a real outcome.** A `wiring`-risk change passes the liveness canary — the
  leaf restarts perfectly — while severing this API's link to it. After such a change the broker
  compares any `pairedApiKey` against this API's own resolved setting and polls reachability, then
  reports honestly instead of claiming success. It does **not** auto-revert: the change was asked for,
  and a silent revert would misreport what is running. Reset stays available and needs nothing from the
  leaf.

Note the two polkit rules are separate on purpose: `48-kgsm-api-deploy.rules` lets **you** deploy,
`49-kgsm-api-leaf-restart.rules` lets **the running service** restart leaves.

`scripts/smoke.sh` is the **stand-in for the frontend** until the SPA can reach a host —
it asserts every M0/M1/M2/M3 contract (and the M4·a no-token sweep) — **31/31**. The M0–M3 checks
run under `Api__AuthDisabled=true` (the escape hatch — synthetic admin) so they exercise the domain
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
external monitor. **M2** is covered by an embedded **SSE reader** (a plain `curl`/fetch-style
`text/event-stream` read against `?topics=`, no external dependency) that subscribes, reads honest
ticks, and — killing **then restarting** the stub monitor mid-stream — proves the degrade→recover
capability lifecycle (down flip + tick silence, then operational flip + ticks resume,
`provisioned:true` throughout).
Knobs: `SMOKE_PORT`, `SMOKE_SKIP_BUILD=1`, `SMOKE_DB`, `SMOKE_KGSM_PATH` (the engine on another
host), `SMOKE_MONITOR_SOCKET` (a live monitor in Phase A).
**`kgsm-api.settings.json` declares the whole configurable surface** — every key with its
default, under one `Api` section that `ApiSettings` binds 1:1. It covers host identity, the kgsm
engine path/journal, the monitor/watchdog/assistant/scheduler/firewall endpoints, the bind address
and DB path, the CORS allowlist, and the auth keys (the Discord application, the account store, and
how long an answer from it is reused). `ApiOptions.FromSettings` is the one place any of it is interpreted; nothing reads
configuration by string key.

**Who someone is comes from a shared application; what they may do comes from their KGSM account.**
The host's OAuth applications live once, in `/etc/kgsm/kgsm-auth.env`, bound from the shared
`KgsmAuth` section that `TheKrystalShip.KGSM.Auth` owns. They are keyed by provider
(`KgsmAuth__Providers__discord__ClientId`), so wiring this host to another provider is a pair of
keys and no rebuild. Every unit loads that file *before* its own env file, so a leaf can still
override deliberately — and doing so means two surfaces signing people in through different
applications, so prefer the shared file. Nothing in that file grants anything: authority is the
account store (`Api__UsersDbPath`), read on every request.

**A provider is a route value, resolved against `IAuthProviderCatalog`** (`Services/Auth/`). It is
registered once per provider in `Startup`, and that registration is the **only** place this API names
one — `/auth/{provider}/start|callback` and `/auth/identities/{provider}/start|callback` take the
name off the route, so adding a provider touches the composition and nothing else. A provider this
host holds no application for and one nothing has ever registered are the **same answer**, a `503
auth_unconfigured`, never a 404 — otherwise the set of providers a build knows about could be probed.
Anonymous `GET /auth/providers` reports the configured set, so the login page draws a button only
where a bounce would work.

This host's own OAuth callback is **not** shared and stays `Api__DiscordRedirectUri`. Its **origin**
is what every provider's two callbacks are built from — the paths are this API's own routes, so a
host's public address is written once and two callbacks cannot name different origins. ⚠ A provider
accepts only redirect URIs registered on the application, so **both** of a provider's callbacks have
to be registered with it or linking is refused at the provider, where no log here sees it.

An environment variable **overrides one key** by spelling that key's path with `__`
(`Api__DomainPollMs`, `Logging__LogLevel__Default`, `Kestrel__Certificates__Default__Path`), and env
wins because it is registered last — that is how the systemd unit and the smoke configure a host. A
variable naming a key the file does not declare **binds to nothing**, and the build fails if the
settings file and `ApiSettings` disagree in any direction, or if `deploy/kgsm-api.env.example` sets a
key the settings file never declared. A blank leaf endpoint reports its capability `absent`.

**`deploy/kgsm-api.leaf.json` is generated, not written.** `TheKrystalShip.KGSM.LeafConfig` rewrites
it on every build from `[LeafField]` attributes and `<panel>` doc tags on `ApiSettings` — so edit the
settings class, never the JSON, and commit what the build produces. A settings key nothing describes
fails the build naming it; `MetricsThresholds` and `AllowedHosts` are declared exempt, the first
because a list of rule objects is not something one variable can deliver. This API is the one leaf
whose descriptor says `readOnly` — applying a change here means restarting the process serving the
request. Mechanism: `../kgsm-leafconfig/README.md`.

Two consequences worth knowing: **secrets are declared blank here and set for real only in the
root-owned `/etc/kgsm-api/kgsm-api.env`**, and the boolean knobs take **`true`/`false` only** — the
older `1`/`0`/`yes`/`on` spellings are refused at startup with an error naming the key. The Discord
application is **shared external config** (the same one the host's Discord bot and assistant use) —
configuration, not a process dependency on kgsm-bot (keystone §4). **`tests/Api.Tests/`** (xUnit + `WebApplicationFactory`,
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
  not a leaf**: provisioned-by-default at `Api__KgsmPath` (`/usr/bin/kgsm`); an empty path is
  a surfaced misconfiguration (empty `/servers` + a one-time log), not a §4·b capability. The
  process-based `IInstanceService` is transient → resolved per-request from the provider. **M5** reads the
  kgsm **event journal** (`Api__KgsmJournalDir`) via kgsm-lib's `IEventService` — `KgsmAuditConsumer`
  tails a directory the engine writes and every consumer reads, so nothing is reserved here and nothing
  needs configuring on the engine side. It starts at the **tail with no cursor**: the API never persists
  an engine event, it publishes each one live (SSE + notifications), so a replay would re-announce what
  was already announced — and nothing is lost, because the durable record is kgsm-monitor's and
  `GET /audit` merges it from there. M3's command path also **stamps**
  `(actor, origin)` on `ILifecycleService.Start/Stop/Restart` (kgsm-lib **1.8.0**) so the engine event —
  and the audit row M5 writes from it — carries who/through-what; the API never writes an audit row for
  its own command (kgsm owns `server.*` → no double-write, see §5 below).
- **Monitor** (host + per-instance metrics) → **scrape its unix socket**
  (`/run/kgsm-monitor/metrics.sock`, `GET /metrics`) directly — that's the monitor's neutral public
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
  (`GET /health`) for the §4·b capability and the HTTP/SSE relay behind `/api/v1/assistant/*`. Probe
  self-bounds via a linked token — leave the client's `Timeout` at default so slower calls aren't
  capped by the probe budget.
  **The relay is peer transport and is expected to be idle.** A browser talking to *this* host's
  assistant addresses the leaf directly, on the public origin reported as the capability's
  `info.url` (`Api:AssistantPublicUrl`), with a session the leaf itself issued — this API relays
  nothing for its own node, and the controller logs a warning on every call it serves so that
  dormancy is measured rather than assumed. `Api:AssistantBaseUrl` is this API's own loopback route
  and is never a browser address; the two are separate settings because conflating them hands a
  browser an address it cannot reach.

**Leaf health & the capability model (M2).** Capability **availability** is owned by the always-on
**`Services/Leaves/LeafHealthMonitor.cs`**, which polls each *provisioned* leaf's health every ~2s
(monitor + assistant `GET /health`; watchdog `IsReadyAsync` via kgsm-lib — never a direct socket).
It is the **single source** feeding both the REST `GET /hosts` capability block (`HostAggregator`
reads its cached `Current`) and the M2 `hosts/{id}/capabilities` stream (it publishes flips). Two
axes, never conflated: **`provisioned`** (the capability *set*) is **runtime-flippable** — seeded at
startup from config, then an admin can connect/disconnect a leaf live from the Services panel (a
DB-backed `LeafRegistry` the `LeafHealthMonitor` reads each tick; the `hosts/{id}/capabilities` patch
carries the changed *set*, not just each capability's `status`); **`status`** is the live
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
   operational metadata — the append-only **audit log** (M5) and the **session registry** (M4·c,
   revocation state — see `Services/Auth/CLAUDE.md`; identity itself stays in the JWT, no user
   row) via EF; the domain is live-scraped, never stored. KGSM stays stateless (the watchdog
   is the lone resident exception, and it's engine, not this API). **The audit is event-sourced,
   single-writer, no double-write:** kgsm owns `server.*`/`backup.*`, so the API records the
   engine's event **echo** (`KgsmAuditConsumer` → `AuditService`) — it never writes a row when it
   *issues* a command; the command path only **stamps** `actor`+`origin` onto the engine call so
   they ride the event. `auth.*` (no kgsm event) is written directly. **Never** add a second writer
   for an action kgsm already emits, and never derive `origin` from the actor — they are independent
   axes (a missing origin is `null`, never fabricated). Schema is **`EnsureCreated`, not an EF
   migration** (dev authority — wipe the DB on a schema change). See `Services/Audit/CLAUDE.md`.

## Conventions

- **JSON:** camelCase + ISO-8601 UTC **`Z`** timestamps, configured once in `Json/ApiJson.cs`
  and applied to both MVC and HTTP options. Add new `DateTimeOffset` fields and they inherit
  `Z` automatically.
- **Errors:** every non-2xx returns the frozen envelope `{ "error": { "code", "message",
  "details?" } }` (`architecture.html §6`) — via `ApiExceptionHandler` (500s) and
  `UseStatusCodePages` (404, and 401/403 once M4 lands). `/health` is **ours** (ops), not a
  frontend contract.
- **Namespaces** are `TheKrystalShip.Api.*` (ecosystem-wide `TheKrystalShip.*`).
- **Versioning (two axes — don't conflate):** the **route version** is the `/api/v1` path segment
  (`ApiInfo.ApiVersion = "v1"`, surfaced as `version`/`panelVersion`) — additive-only, changes only on a
  breaking generation. The **build version** is the assembly InformationalVersion = `<Version>` (in
  `Api.csproj`, currently `0.1.0`) **+ the git SHA auto-stamped by the `SetSourceRevisionId` target**, e.g.
  `0.1.0+<sha>` — surfaced as `build` on `GET /api/v1` and `identity.build` on the Host DTO (the honest
  "which build is this host running"). Bump `<Version>` per release; the SHA degrades to absent (never
  fabricated) outside a git checkout. **Full reference: `README.md` §Versioning.**
- **Logging:** the ecosystem convention (`../logging-convention.md`) — the host does
  `ConfigureLogging(ClearProviders → AddSystemdConsole)`; levels come from the `appsettings.json`
  `Logging` section + env (`Logging__LogLevel__Default`). ⚠ The notification-webhook `HttpClient`
  keeps `.RemoveAllLoggers()` (Startup) — that's load-bearing secret-redaction, never drop it.
- **Validation model:** each milestone ends at a **frontend gate** — agree the wire shapes
  first (`§6`), build + self-prove (smoke + a live leaf), then the frontend swaps its store
  mock → real. Caution on the wiring; this is the first time frontend + backend + leaves
  merge.

## Version tracking

- **Version source:** `<Version>` in `src/Api/Api.csproj`; the build automatically appends the short git SHA to `AssemblyInformationalVersion`
- Bump the version whenever you make a user-facing change (new feature, bug fix, behaviour change). Patch for fixes, minor for new features, major for breaking changes.
- Update `CHANGELOG.md` under `## [Unreleased]` with a brief entry for every meaningful change.
- A git tag matching the new version should be created on release: `git tag v<version>`.

## Gotchas

- **`legacy/`** is the scrapped .NET 9 API — harvest patterns (e.g. log-streaming) but
  treat nothing in it as correct (it fabricates metrics).
- **EF `EnsureCreated`, NOT migrations (settled at M5, user directive 2026-06-15).** Greenfield/dev
  authority: the schema (`AuditEntry`) is created via `EnsureCreatedAsync` (no `__EFMigrationsHistory`),
  and a schema change means **wiping the dev DB**, not adding a migration. ⚠ `EnsureCreated` **no-ops on
  an existing DB** — so after any entity change, delete the DB file (smoke `rm -f`s its own `SMOKE_DB`)
  or the new column/table silently won't exist and queries 500 at runtime, not build. Don't introduce
  `Migrations/` without re-deciding this. The M0 `Probe` table is gone (replaced by `AuditEntry`);
  `_dbcheck` is a **read** round-trip (the append-only audit table must never be probe-written).
- **Diagnostics endpoints** (`/api/v1/_throw`, `/api/v1/_dbcheck`) are smoke-only probes —
  remove/restrict before any public exposure.
- **Trust window:** M3 (commands, which mutate) lands before M4 (auth) — **CONFIRMED acceptable**
  (user, 2026-06-15) **only** on a trusted, non-public network until M4. The M3 write path is
  unauthenticated by design this milestone; the gate enforces state guards only (permissions at M4).
  See `PLAN.md` M3.
- **`SuppressMapClientErrors=true`** (Startup): `[ApiController]` would otherwise turn a
  controller `NotFound()`/`BadRequest()` into RFC-9110 ProblemDetails. We suppress it so 4xx
  flow through `UseStatusCodePages` → the `{error}` envelope (one error shape everywhere).
  **⚠ RESOLVED at M8·b:** `SuppressMapClientErrors` only covers *result*-based 4xx — a model-binding/
  validation `400` (malformed JSON, or a body field of the wrong type, e.g. `InstallRequest`'s typed
  `int?`/`bool?` reserved fields) is rejected by `[ApiController]` **before the action runs** and emitted
  `ValidationProblemDetails`. Now Startup's `ConfigureApiBehaviorOptions` sets an
  `InvalidModelStateResponseFactory` that returns the frozen `{error:{code:"bad_request"…}}` envelope
  (regression-tested, type-mismatch + malformed JSON). **Don't remove it** — it's what keeps invariant #4
  (every non-2xx is the envelope) true for any typed request body, here and on every future POST/PATCH.
- A finished feature is committed without being asked (the ecosystem-wide rule — see the workspace
  `CLAUDE.md`). Pushing still needs an explicit request.
