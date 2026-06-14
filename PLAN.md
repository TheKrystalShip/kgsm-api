# KGSM Control Panel API — Milestone Plan & Design Record

> Living document. The staged road to **v1.0** of the per-host Control Panel
> backend (`TheKrystalShip.Kgsm.Api`) — the aggregator that joins this host's
> kgsm-lib (domain + supervision), kgsm-monitor (metrics) and the assistant
> behind one Discord-OAuth'd surface for the React SPA.
>
> **This is the first time the frontend, the backend, and the leaves are wired
> together.** So the plan is deliberately incremental: each milestone is a small,
> shippable *wiring increment* that ends at a **frontend validation gate** — the
> frontend team swaps one or more stores from mock → real and we confirm the
> contract holds before moving on. Caution on the wiring, optimism on the pace.
>
> Companion docs: `../system-architecture.md` (the keystone — topology, invariants,
> ownership), `../architecture.html` (the frontend's external-surface proposal,
> currently **v0.3**), `../assistant-toolbox-plan.md` (the assistant + tool catalog).

---

## Status legend
`built` = exists & verified · `partial` = exists, incomplete · `planned` =
designed, not built · `open` = not yet decided.

**Today:** M0 `partial` and **M1·a `partial`** (backend built & self-validated; frontend
gate pending). The skeleton (controllers + EF Core, **JIT**; §8) and the first leaf wiring
— `GET /hosts` + `/hosts/{id}` scraped from kgsm-monitor with the §4·b capability block —
are built and `scripts/smoke.sh` is **12/12** (8 M0 + 4 M1·a), proven both ways: degrade
path (no monitor → metrics `down`, capacity `null`) and happy path (live monitor → real
host capacity + a real `operational` watchdog probe). The superseded .NET 9 attempt is
parked in `legacy/` for harvest (keystone O4). Pending: the live frontend handshake + venue.
The two read-side inputs it aggregates are `built` and ready: kgsm-lib 1.6.0 (domain +
run-state façade + `IWatchdogClient`) and kgsm-monitor (host + per-instance metrics over
`/run/kgsm-monitor.sock`), the latter now consumed through the shared
`TheKrystalShip.KGSM.Monitor.Contracts` package (§7). The assistant is `partial`.

---

## 1 · What we are building (and what we are not)

A **per-host** backend. The deployable unit is *one host* = `kgsm` + any subset of
leaves + this API (keystone §4). The API aggregates **only its own host's** leaves;
the **SPA** fans out to N hosts and does all cross-host rollup **client-side**
(`architecture.html §4·a` — no `/fleet/summary` endpoint exists or is needed at
homelab scale). This is settled (confirmed 2026-06-14) and follows from four
independent v0.3 signals: per-host WebSocket (§3·b), per-host auth/bot (§3·f),
per-host capabilities (§4·b), client-side aggregation (§4·a).

**Runtime:** .NET 10, `Microsoft.NET.Sdk.Web`, **Native AOT** (`PublishAot`,
`IsAotCompatible`), source-generated JSON — matching the rest of the ecosystem
(lib/monitor/watchdog). The one historically-rocky AOT area is **OAuth/auth
middleware**; we spike that *before* the auth milestone, never assume it (M4).

**Namespace:** `TheKrystalShip.Kgsm.Api` (parallels `TheKrystalShip.Kgsm.Assistant`).
**Repo:** rewrite in-place here — fresh `src/`, keep repo + remote.

### Non-goals / explicitly deferred
- **No cross-host aggregation in the backend** — the SPA does it (§4·a).
- **No fabricated metric/status/alert — ever.** Measured, or typed-`unknown`
  (keystone §4). The aspirational fields in `architecture.html`'s `Server` example
  (`cpu` 0–100, `ram.max`, `players`, `ip`) have **no backing source today**; we
  emit what's real and negotiate the rest with the frontend at M1 (see §4).
- **Per-instance network metrics** (eBPF/netns) — deferred upstream in the monitor.
- **Time-series metrics *history*** (`GET /servers/{id}/metrics?range=1h`) needs a
  **metrics store** the monitor doesn't provide (it serves *latest* only). Treated
  as its own decision (§5, "Open"), not bundled into the early read milestones.
- **Voice transcription** — out of scope for v1 (`architecture.html §3·i`).

---

## 2 · Principles (carried from the keystone, applied here)

- **Server is authoritative; never fabricate.** Honest unknowns over invented data.
- **Metric-presence ≠ status, and status-presence ≠ status.** Run-state comes from
  kgsm-lib's façade (`Reading<InstanceRuntimeStatus>` — which can itself be
  `unknown`); metrics come from the monitor; absence of either is "not measurable
  now," never a coerced default.
- **Leaves are independently deployable; aggregation is additive & degrades
  gracefully.** A missing leaf removes only its capability (the §4·b capabilities
  contract is exactly this, made first-class). The API depends on leaves; **no leaf
  depends on the API**.
- **Additive-only within `/api/v1`.** Lock schemas forward-compatibly; grow into
  reserved fields with no version bump (`architecture.html §6`, §3·h).
- **Commands are gated requests, not executions** — admissibility gate → human
  confirm → job → verify (`architecture.html §5·d`). Closed, server-defined verb &
  action vocabularies; clients/the model can't invent one.

---

## 3 · The collaboration model (how each milestone is validated)

Every milestone ends at a **frontend validation gate**, run jointly:

1. **Contract freeze (before build).** Backend + frontend agree the exact wire
   shapes the milestone touches — DTO fields, WS message envelope, error envelope —
   recorded in the §6 contract registry. This is where the *honest-vs-aspirational*
   negotiation happens (most pointedly at M1).
2. **Build + self-prove.** Backend builds the increment, unit-tests the join/logic
   with mocked leaves, and **live-proves once** against the dev `kgsm` + a running leaf
   (curl-mocked via `scripts/smoke.sh` until the SPA can reach a host).
3. **Swap mock → real.** Frontend flips the affected store(s) from the
   `window.KRYSTAL_DATA` mock to real `fetch`/WS (`architecture.html §7`). Where the
   honest DTO *matches* the mock, this is a pure backing-flip with no component changes.
   Where it **diverges** — most sharply at M1·b, where we emit `cpuPctCore` and omit
   `players`/`ip`/`ram.max` — the swap also needs **rendering changes**; that's expected
   and flagged on the affected milestone, not a surprise at the gate.
4. **Confirm & record.** Both teams confirm; deviations update the registry and the
   keystone's open-decision ledger. Then the next milestone unlocks.

Migration order mirrors `architecture.html §7`: alerts (already mock-proven) →
servers + jobs → metrics/console (WS) → diagnostics → audit → library → settings.

---

## 4 · The milestones

> Each: **Goal · Leaf/contract wired · Scope · Depends on · Risk/caution ·
> Frontend gate.** Sized so M0–M1 are small and concrete; later ones are the
> roadmap, refined as we approach them.

### M0 — Handshake, skeleton & the runtime/stack decision  ·  `partial`
> **Backend built & self-validated 2026-06-14** (controllers + EF Core, JIT; `scripts/smoke.sh`
> 8/8; see §8). Outstanding before `built`: the live frontend handshake and the
> validation-venue decision (deferred — no frontend access yet; mocked with `scripts/smoke.sh`).
- **Goal:** prove the SPA ↔ API plumbing *and* settle the runtime/stack bet before a
  single milestone is built on it.
- **Wires:** SPA ↔ API only.
- **Scope:**
  - Project stands up; `/api/v1` base; `/healthz`; the standard error envelope
    `{ error: { code, message, details? } }`; CORS to the SPA origin; JSON conventions
    (camelCase, ISO-8601 UTC `Z`, opaque string ids); the auth pipeline **deferred to M4**
    (`UseAuthentication/UseAuthorization` placeholder). One trivial real read to exercise
    the path, plus an EF+SQLite round-trip probe.
  - **Runtime/stack decision (settled here, deliberately).** A throwaway spike first
    proved Native AOT *viable* for the rocky areas (WebSockets, SQLite, OAuth bearer). We
    then chose **standard JIT** anyway, to unlock the conventional **MVC controllers +
    EF Core** stack — AOT forbids both (verified: "MVC does not support native AOT", "EF
    Core isn't fully compatible with NativeAOT"). The trade (maintainability over the
    single-native-binary deploy; AOT foreclosed for the API) was made eyes-open — full
    record in §8 and `docs/m0-aot-spike-findings.md`. kgsm-lib stays AOT-safe and is
    consumed unchanged (AOT code runs fine under JIT); ecosystem correctness is intact.
  - **Validation venue (the literal first-wiring question).** Decide and record *where
    the shared `kgsm + monitor + API` instance runs and how the frontend reaches it*
    (the dev host + a tunnel/origin, or a co-located dev deploy). Every later gate is
    "frontend swaps mock → real against a running instance" — that instance has to exist
    and be reachable, or M1 can't be validated.
- **Depends:** nothing.
- **Risk:** low on the skeleton; the runtime/stack decision *is* the thing to settle
  early. CORS, base path, content-type and error shape are expensive to retrofit, so get
  them right once.
- **Frontend gate:** SPA fetches the API cross-origin against the agreed venue, parses
  the error envelope, renders "backend reachable." Agree base URL / CORS / versioning /
  error shape. Runtime/stack decision recorded in §8.

### M1 — Read-only aggregation  ·  `planned`  ←  *first real slice (split to start small)*
The per-instance metrics join + the degrade-gracefully contract, REST-only (SPA polls;
realtime is M2). This is the lane that motivated the whole project (the performance half
of `trace_root_cause`). Split into two sub-increments so the *smallest possible real-data
wiring* lands first.

**M1·a — Hosts-first (pure scrape, no join).**  ·  `partial` (backend built & self-validated 2026-06-14; frontend gate pending)
- **Goal:** the smallest real increment — prove the monitor wiring alone.
- **Wires:** **kgsm-monitor leaf** only (scrape `/metrics`, cached-latest, reusing the
  watchdog client's `SocketsHttpHandler.ConnectCallback`). The snapshot is deserialized
  into the **shared `TheKrystalShip.KGSM.Monitor.Contracts`** types with the monitor's own
  source-gen JSON context — producer and consumer share one build-time contract (§7).
- **Scope:** `GET /hosts`, `/hosts/{id}` (host capacity straight from the snapshot —
  maps 1:1 to §4·a `cpuPct`, `mem{used,total}`, `disks[]`) + the §4·b capabilities block
  (probe monitor / watchdog `IsReadyAsync` / assistant HTTP → `provisioned·operational·
  down·absent`). No kgsm-lib join yet.
- **Built (2026-06-14):** `ApiOptions` (config via `IConfiguration`: host identity + leaf
  endpoints, every key documented in `appsettings.json` and overridable by a same-named env
  var; provisioning derived from whether each endpoint is configured), `MonitorClient`
  (cached-latest scrape, bounded ≤1s, fails closed to `null`), `HostAggregator` (one
  scrape → coherent capacity + metrics capability; watchdog/assistant probed concurrently,
  each bounded to 2s so a hung leaf never stalls `/hosts`), `HostsController`. The assistant
  capability is probed through a dedicated typed `AssistantClient : HttpClient` (its liveness
  probe self-bounds to 2s via a linked token, leaving the client's own `Timeout` free for the
  slower tool/capability/SSE calls it grows at M7). Honest
  mapping: capacity is **null when metrics ≠ operational** (KiB→GiB and bytes→GiB
  conversions; `cpuPct` passed through). Capability provisioning is config-driven (empty
  endpoint → `absent`); identity is config-driven (default machine name), never the
  monitor's flappy `hostname`. `SuppressMapClientErrors=true` so a controller `404`
  returns the `{error}` envelope, not framework ProblemDetails.
- **Self-validated:** `scripts/smoke.sh` 12/12 — degrade path (default, no monitor) and
  happy path (`SMOKE_MONITOR_SOCKET`/`SMOKE_WATCHDOG_SOCKET` → real capacity + real
  `operational` watchdog), with the **metrics-status ↔ capacity coupling** asserted both ways.
- **Frontend gate:** `hostsStore` swaps mock → real; the fleet capacity strip + the
  capability dials render from real data. **Not a clean backing-flip** — see the §6
  divergences from the §4·a/§4·b examples (capacity is nullable; `info.intervalMs` ms not
  `interval_s` s; `since`/`transport` omitted).

**M1·b — Servers (the join).**
- **Goal:** join domain + status (kgsm-lib) with per-instance metrics (monitor).
- **Wires:** adds kgsm-lib (embedded, `GetAllStatuses` + `GetAll`); join on instance id
  (verified: monitor `ServerMetrics.Id` == the kgsm instance name == the lib dict key).
- **Scope:** `GET /servers`, `/servers/{id}`. **Honest DTO:** status tri-state from
  `Reading<T>` (`running` / `stopped` / `unknown`); metrics `cpuPctCore` (**preserved
  unit** — % of one core, can exceed 100), `memBytes`, nullable `io*`, `pids`, or **null**
  when the monitor is absent; every server carries `hostId` (this host).
- **Depends:** M0, M1·a.
- **Risk:** **the honest-DTO negotiation.** The v0.3 `Server` example asks for
  `cpu`(0–100), `ram.max`, `players`, `ip` — none honestly sourceable today; building
  them verbatim = fabrication (the sin that scrapped the old kgsm-api).
- **Frontend gate:** `serversStore` swaps mock → real. **This is not a clean backing-flip:**
  the honest DTO *diverges* from the mock (`cpuPctCore` not `cpu`; no `players`/`ip`/
  `ram.max`), so the frontend's server cards need **rendering changes**, not just a source
  swap — set that expectation up front. Reconcile the aspirational example against backable
  fields and **freeze the real `Server` DTO here.** The single most important contract
  conversation in the project.

### M2 — Realtime: WebSocket per host  ·  `planned`
- **Goal:** push the M1 data instead of polling (resolves keystone O2 → WebSocket).
- **Wires:** monitor tick → push; kgsm-lib status-change → push.
- **Scope:** `/api/v1/stream`; `{ topic, type, data }` envelope; subscribe/unsubscribe;
  topics `servers`, `servers/{id}/metrics`, `hosts/{id}/metrics`,
  `hosts/{id}/capabilities`; the §3·j resilience handshake (per-host reconnect/backoff,
  poll-fallback, re-hydrate on return).
- **Depends:** M1 (same DTOs, now streamed).
- **Risk:** WebSocket lifecycle/backpressure and the message-envelope contract (the
  ASP.NET Core WebSocket middleware is bog-standard under JIT).
- **Frontend gate:** `realtimeStore` connects, applies patches, falls back to polling
  on drop and snaps back on reconnect.

### M3 — Commands: gate → confirm → job → verify  ·  `planned`
- **Goal:** the first write path — lifecycle actions.
- **Wires:** **kgsm-watchdog leaf** (via kgsm-lib: native→watchdog, container→Docker).
- **⚠ Trust-window assumption (explicit, must be confirmed at this gate):** M3 mutates a
  real host (start/stop/restart/update) a full milestone **before** M4 authenticates.
  This is acceptable **only** because the API is validated on a **trusted local network
  and is not exposed publicly until M4 lands**. If the frontend team cannot guarantee
  that during M3 validation, **pull M4 auth forward to gate M3**. Do not skip this
  confirmation — it's a safety boundary, not a nit.
- **Scope:** `POST /servers/{id}/commands { verb: start|stop|restart|update }` → `202`
  + `job`; `jobs` WS topic for progress/completion; the **admissibility gate** (state
  guards, later permissions); `command.verified` re-check when the job settles. Closed
  verb set.
- **Depends:** M1/M2; M4 auth can be pulled forward to gate M3 if the trust window can't be guaranteed.
- **Risk:** real mutation — the gate, idempotency, and (once M5 lands) the audit write
  must be right. Optimistic-UI reconciliation contract.
- **Frontend gate:** confirm the trust window above; then action buttons → optimistic
  transitional state → job tracking → reconcile to authoritative status.

### M4 — Auth: Discord per-host (Model A)  ·  `planned`
- **Goal:** the security boundary — make good on the trust window M3 ran under.
- **Wires:** Discord (external IdP, silent SSO) + this host's bot (role → tier).
- **Scope:** `/auth/discord/callback`, `/auth/session`, `/auth/session/refresh`,
  `/auth/logout`; per-host bearer (short TTL, proactive refresh, 8h cap); tiers
  `admin·operator·viewer·none`; the `401`/`403`/`login_required` state machine
  (`architecture.html §3·f`); protect all prior endpoints. JIT unlocks the conventional
  ASP.NET Core auth pipeline (`UseAuthentication`/`UseAuthorization`, `[Authorize]`,
  policy-based tiers); the Discord OAuth code flow + a session/bearer store (EF
  `sessions` table, or JWT) — **decide the bearer mechanism here.**
- **Depends:** M0 (the auth-pipeline placeholder). Must land before any public
  exposure; tier-gating of M3 commands lands here.
- **Risk:** the security boundary itself.
- **Frontend gate:** the per-host session state machine end-to-end; tier-gated controls.

### M5 — Audit log + SQLite (the event-persistence consumer)  ·  `planned`  ←  *resolves keystone O3*
- **Goal:** the durable, append-only action record — **persistence downstream of the
  stateless engine**, exactly where O3 says it belongs (a consumer, never KGSM).
- **Wires:** **kgsm event socket** (kgsm-lib `EventService`); the `Actor` enrichment
  shipped in kgsm-lib 1.6.0 feeds the audit `actor`.
- **Scope:** subscribe to kgsm lifecycle events → map to the closed dotted `action`
  vocabulary (`server.*`, `config.change`, `network.ports.open`, …) → append via EF Core
  to the audit table (`architecture.html §3·d` schema, keyset pagination on `rowid`) →
  `GET /audit` + `audit` WS topic. M3 commands and M4 auth also write audit entries.
  First milestone to add an EF migration (the M0 `Probe` probe is replaced by the real
  schema here / at M4). **⚠ Start migrations from a clean DB:** M0's `_dbcheck` uses
  `EnsureCreated` (no `__EFMigrationsHistory`), so a DB it created conflicts with
  `ef database update` — drop the dev DB when the first migration lands.
- **Depends:** M3 (commands to record), M4 (actor identity), event enrichment (`built`).
- **Risk:** append-only immutability discipline; EF migration hygiene; fidelity of the
  kgsm-event → action mapping.
- **Frontend gate:** `auditStore` prepends on `audit.append`; filters map to indexed columns.

### M6 — Alerts (condition-mirror) + ports  ·  `planned`
- **Goal:** the needs-attention surface + the one-click firewall fix.
- **Wires:** monitor thresholds + watchdog crash signals → alerts; kgsm-lib firewall/UPnP
  (+ the watchdog-network-delegation plan) → ports.
- **Scope:** alerts `raise/resolve/retract` with debounce → probation → escalation
  (`architecture.html §3·c`), `GET /alerts?status=firing|resolved`, `alerts` WS, the
  alert↔audit bridge (`resolution.actionId`). Ports: the `network` block on
  `/servers/{id}` (blueprint-required ⋈ firewall-probed-open ⋈ `reachable`),
  `/hosts/{id}` open-ports grid, `POST .../commands { verb: open_ports }`
  (**intent-only — no client port list**; server-derived target; re-probe verify; audited).
- **Depends:** M5 (audit bridge), M3 (the open_ports command), monitor/watchdog signals.
- **Frontend gate:** `alertsStore` (the prototype-proven shape) + the network card / open-ports flow.

### M7 — Assistant turn relay  ·  `planned`  ←  *resolves keystone O1*
- **Goal:** the AI surface — relay the assistant service's turn stream.
- **Wires:** **assistant leaf** (its own `/turn` SSE; proxy-verbatim is now *enabled*
  because the assistant emits the canonical typed vocabulary — keystone O1).
- **Scope:** `POST /api/v1/assistant/turn` relays the assistant's SSE
  (`text.delta`/`tool.start`/`tool.result`/`command.proposed`/`command.verified`/
  `done`/`error`), capability-gated per host. **Decide proxy-verbatim vs re-wrap here
  (closes O1).**
- **Depends:** M4 (auth), the assistant service (`partial` — may need a readiness pass).
- **Risk:** SSE relay/streaming correctness; assistant maturity.
- **Frontend gate:** the thread renderer against real SSE; the dock's per-host assistant
  picker honors the `assistant` capability.

### M8 — Install · library · cover art · settings/integrations  ·  `planned`
- **Goal:** the create operation + the config surfaces.
- **Wires:** kgsm-lib blueprints; RAWG (external, server-side, key off-browser); the
  Discord integration routing layer.
- **Scope:** `GET /library` (+ server-side cover resolution), `POST /servers`
  (`blueprint` + `name` honored, the rest accepted-but-inert per §3·h), the install
  job + `server.install` audit; `/settings`, `/me`, `/integrations/discord` (+ `/test`).
- **Depends:** M3 (jobs), M5 (audit).
- **Frontend gate:** the install form (handle the "collected but inert" fields honestly)
  + settings/integration panels.

---

## 5 · The v1.0 bar

**"First usable" marker (the early integration win to celebrate) — after M4:** the SPA,
on one real host, shows live server/host state (M1), streamed (M2), can run one gated
lifecycle command end-to-end (M3), behind Discord auth (M4). That's a genuinely usable
control panel and the proof the frontend+backend+leaf wiring holds — well before the
full bar.

**v1.0 = a per-host Control Panel API that, running on a real host alongside the SPA:**
serves honest aggregated reads (M1), streams them (M2), executes gated + audited +
verified commands (M3), enforces Discord per-host authorization (M4), persists and
serves the audit log (M5), mirrors alerts and fixes ports (M6), and relays the
assistant (M7) — **every milestone frontend-validated, shipping as a self-contained JIT
build, and the invariants (per-host, honesty, leaf-independence, degrade-gracefully)
verified live.**

M8 (install/library/integrations) is a **collaborative call** at the M7 gate: ship in
v1.0 or as a fast-follow v1.1, depending on how the integration is holding together.

**Open decisions to resolve along the way:**
- ~~AOT vs JIT~~ — **RESOLVED at M0: standard JIT** (controllers + EF Core), traded for
  maintainability; see §8. The forward milestones no longer carry AOT cautions.
- **Bearer mechanism** (EF `sessions` table vs JWT) — decide at M4.
- **Metrics-history store** (for `GET /servers/{id}/metrics?range=…`) — does the
  backend persist monitor ticks (a second SQLite table / ring buffer), or does the
  monitor grow a history buffer? Decide before any time-series chart milestone.
- **Proxy-verbatim vs re-wrap** for the assistant SSE (O1) — decide at M7.

---

## 6 · Cross-team contract registry (the shapes both teams agree)

Frozen here as each milestone's gate is reached; the authority is `architecture.html`
for the external surface and this doc for the backend's honest realization of it.

| Contract | Milestone | Owner / notes |
|---|---|---|
| Error envelope `{ error:{ code, message, details? } }` | M0 | `architecture.html §6` — **frozen, self-validated (500+404); browser fetch pending** |
| Base path `/api/v1` (path-versioned, additive-only) | M0 | `architecture.html §6` — **frozen** |
| JSON conventions: camelCase · ISO-8601 UTC `Z` · opaque ids | M0 | `architecture.html §6` — **frozen; camelCase + `Z` via shared JSON options (MVC + HTTP), verified** |
| `Server` DTO (honest realization) | **M1·b** | reconcile vs §3 example; preserve `cpuPctCore`, null the unsourceable |
| `Host` DTO + capacity (`cpuPct`/`mem`/`disks`) | M1·a | `architecture.html §4·a` — **frozen 2026-06-14.** `{ id, label, status:"online", cpuPct, mem{used,total} GiB, disks[]{mount,used,total} GiB, capabilities }`. **Divergence (record for the gate):** capacity (`cpuPct`/`mem`/`disks`) is **nullable** — `null` when metrics ≠ operational (the §4·a example always shows numbers). `/hosts/{id}` currently == the list shape; §244's sensors/network/processes are deferred. |
| Capability record `{ provisioned, status, since?, message?, info? }` | M1·a | `architecture.html §4·b` — **frozen 2026-06-14.** status ∈ `operational|degraded|down|unknown`; `provisioned:false` → client-derived `absent`. M1·a emits `operational`/`down` (+ `absent`); `degraded`/`unknown` arrive with the M2 stream. **Divergences:** `info` keys are camelCase and **`info.intervalMs` (ms) replaces the example's `info.interval_s` (seconds)** — a name *and* unit change, faithful to the monitor's native field; `since`/`last_sample_at` **omitted** (no status-change tracking until M2); `transport` **omitted** (REST now, not `"sse"`). |
| Monitor `/metrics` wire shape (`Snapshot` graph) | M1·a | **shared package** `TheKrystalShip.KGSM.Monitor.Contracts` — the DTO graph + source-gen camelCase JSON, built in kgsm-monitor and consumed here so the contract is solid at build time. **Drift rule:** any contract change MUST bump the package `Version` and the api's `<PackageReference>` (a same-version repack is silently served stale from the NuGet cache). |
| WS message envelope `{ topic, type, data }` + topic set | M2 | `architecture.html §3·b` |
| Command verbs + `job` shape + `command.verified` | M3 | `architecture.html §5·d` |
| Auth session + tiers + 401/403/login_required | M4 | `architecture.html §3·f` |
| Audit record + closed `action` vocabulary + SQLite schema | M5 | `architecture.html §3·d` |
| Alert record + raise/resolve/retract; `network` block | M6 | `architecture.html §3·c, §3·g` |
| Assistant SSE event vocabulary (proxy vs re-wrap) | M7 | `architecture.html §5·a`; keystone O1 |
| Install body (honored vs reserved) | M8 | `architecture.html §3·h` |

---

## 7 · Project layout (target, as it grows)

Conventional controllers + EF Core on JIT (the §8 decision). Thin controllers; the real
weight lives in `Services/` (leaf clients + join/mapping) and `Data/` (EF) — both
runtime-agnostic. Endpoints group by feature; controllers stay thin.
```
kgsm-api/
  kgsm-api.slnx
  PLAN.md · README.md · nuget.config (local feed: kgsm-lib + kgsm-monitor contracts)
  src/Api/
    Api.csproj                # [M0] net10, Sdk.Web, EF Core Sqlite, JIT, AssemblyName=kgsm-api
    appsettings.json          # [M0] logging defaults (env vars override via IConfiguration)
    Program.cs                # [M0] entry: Host.CreateDefaultBuilder + ConfigureWebHostDefaults().UseStartup<Startup>() (classic structure, no top-level statements)
    Startup.cs                # [M0] ConfigureServices (controllers+JSON, EF, CORS, exception handler) + Configure (pipeline)
    Controllers/              # [M0] Health, Meta, Diagnostics · [M1·a] Hosts → [M1·b] Servers → [M3] Commands → [M5] Audit → …
    Contracts/                # [M0] ErrorEnvelope, HealthStatus, ApiInfo · [M1·a] HostDto (Host/MemCapacity/DiskCapacity/HostCapabilities/Capability) (+ domain DTOs as milestones land)
    Infrastructure/           # [M0] ApiExceptionHandler (IExceptionHandler→500 envelope), ApiErrors (envelope writer); auth handlers [M4]
    Json/                     # [M0] ApiJson (shared options config) + Iso8601UtcConverter
    Data/                     # [M0] AppDbContext (+ Probe de-risk) → [M4] Session → [M5] AuditEntry; Migrations/ from M5
    ApiOptions.cs             # [M1·a] config consolidation via IConfiguration (host id/label, monitor/watchdog sockets, assistant url; keys documented in appsettings.json, env-overridable; *Provisioned derived from config) — built
    Services/
      Leaves/                 # [M1·a] MonitorClient (cached-latest ConnectCallback scrape; deserialize via shared Monitor.Contracts) + AssistantClient (typed HttpClient subclass; liveness ProbeAsync now, grows tools/capabilities/SSE relay at M7) · [M3] watchdog via kgsm-lib
      Aggregation/            # [M1·a] HostAggregator (capacity + §4·b capabilities; bounded concurrent leaf probes) → [M1·b] lib status ⋈ monitor metrics
      Realtime/               # [M2] /stream hub, topic push pumps
      Commands/               # [M3] gate + job tracker + verify
      Auth/                   # [M4] Discord per-host OAuth, bearer/session, tiers
      Audit/  Alerts/ Ports/  # [M5]/[M6] event consumer, alert engine, port intent
      Assistant/              # [M7] SSE relay
      Install/ Library/ Settings/ Integrations/  # [M8]
  tests/Api.Tests/            # xUnit; mock IInstanceService + captured-snapshot fake scrape per milestone
```

---

## 8 · Validation log

### M0 — 2026-06-14 · backend self-validated; frontend gate PENDING
**Status:** the backend deliverables are built and verified (JIT, `dotnet`); the
*collaborative* gate (live SPA handshake) is deferred — no frontend access yet, so it
was stood in for with a curl-based mock (`scripts/smoke.sh`). Not marked "frontend validated."

**DECISION — runtime/stack: standard JIT, controllers + EF Core (not Native AOT).**
A throwaway AOT spike first proved all three rocky areas viable native (WebSockets 101+echo;
`Microsoft.Data.Sqlite` file round-trip; Discord-shaped OAuth JSON + `HttpClient`/form,
reaching `discord.com`→200 so the box has egress). We then **chose JIT anyway**, deliberately,
to unlock the conventional MVC controllers + EF Core stack for long-term maintainability —
AOT forbids both (verified on .NET 10.0.108: "MVC does not support native AOT"; "EF Core
isn't fully compatible with NativeAOT"). Trade accepted: lose the single 14 MB native binary
(self-contained JIT deploy is ~121 MB with ReadyToRun, still no runtime install) and ecosystem
toolchain consistency; gain the mature stack + foreclose AOT for the API. kgsm-lib stays AOT-safe,
consumed unchanged (AOT code runs under JIT). Rationale + spike evidence: `docs/m0-aot-spike-findings.md`.

**Skeleton — self-validated (controllers + EF + JIT).** `scripts/smoke.sh` → **8/8**:
`/healthz` + `/api/v1` (camelCase, **ISO-8601 UTC `Z`** timestamps via the shared
`Iso8601UtcDateTimeOffsetConverter` on MVC + HTTP JSON options), an **EF Core + SQLite
round-trip** (`_dbcheck`), the error envelope on a **real 500** (`_throw`, via
`ApiExceptionHandler`) and a **404** (`{error:{code:not_found}}`, via `UseStatusCodePages`),
and CORS request + preflight headers.

**Contracts frozen from `architecture.html` (verified verbatim, not invented):** error
envelope `{error:{code,message,details?}}` (§6 line 1010); base path `/api/v1`,
path-versioned, additive-only (§6); timestamps ISO-8601 UTC `Z` (§6). `/healthz` is
**ours** (ops), not a frontend contract — the SPA derives liveness from store state (§3·j).

**Owed to the frontend at the gate (when reachable):** confirm base URL / CORS origin /
versioning / error shape against a real browser fetch (CORS is browser-enforced; curl
proves headers emitted, not preflight accepted). **Open — validation venue:** *where the
shared `kgsm + monitor + API` instance runs and how the frontend reaches it.* Proposal:
the canonical dev host (`/home/heisen/tks/kgsm`) with the API bound via `KGSM_API_URLS`
and a tunnel/allowlisted origin via `KGSM_API_CORS_ORIGINS` — to confirm with the team.

**Build/run:** `dotnet build kgsm-api.slnx` · `scripts/smoke.sh` (builds Release + runs
checks). The throwaway `spike/` and `spike2/` are deleted (their findings are durable here
and in the findings doc).

### M1·a — 2026-06-14 · hosts (monitor scrape + capabilities) self-validated; frontend gate PENDING
**Status:** backend built and verified live (real host metrics off this box + a real
watchdog readiness probe); the collaborative gate (frontend swaps `hostsStore` mock → real)
is deferred with M0's (no frontend access). Not marked "frontend validated."

**The shared metrics contract (don't-let-it-drift).** The monitor's `Snapshot` DTO graph +
its source-gen camelCase JSON context were extracted into a new package,
**`TheKrystalShip.KGSM.Monitor.Contracts` 1.0.0**, built in the kgsm-monitor repo
(`src/Monitor.Contracts/`, referenced by the monitor via ProjectReference) and packed to the
local feed. The api consumes the package and deserializes the scrape into the **same types
with the same context** — the wire shape and camelCase naming are one definition, so a
contract break is a *compile* break, never a silent runtime mismatch. The monitor still
AOT-publishes clean (0 ILC warnings) and all 59 monitor tests pass with the extraction.
**⚠ Drift rule:** NuGet caches by `id+version`, so any contract change MUST bump the package
`Version` *and* the api's `<PackageReference>` — a same-version repack is served stale from
`~/.nuget/packages`. Loop: edit contract → bump `Version` → `dotnet pack -o /home/heisen/local-nuget`
→ bump the api ref.

**Honest realization (frozen in §6).** Capacity is `null` when metrics ≠ operational
(KiB→GiB, bytes→GiB; `cpuPct` 0–100 passed through). Host identity is config-driven
(`KGSM_API_HOST_ID`, default machine name) so it can't flap with the monitor; `status:"online"`
is honest (the api answers on the host it runs on). §4·b capabilities: `provisioned` derived
from whether the leaf endpoint is configured (empty → `absent`); `metrics` from the scrape,
`watchdog` from `IWatchdogClient.IsReadyAsync`, `assistant` from a provisional HTTP liveness
probe (real readiness lands at M7). Each leaf probe is independently bounded (≤2s) and the
monitor scrape is a cached-latest pull-through (≤1s, fails closed to `null`) — a hung or
absent leaf degrades only its capability and never 500s or stalls `/hosts`.

**Self-validated:** `scripts/smoke.sh` → **12/12** (8 M0 + 4 M1·a). The M1·a checks run
deterministically with no monitor (degrade path: metrics `down`, capacity `null`, watchdog/
assistant `absent`) and prove the happy path under `SMOKE_MONITOR_SOCKET`/`SMOKE_WATCHDOG_SOCKET`
(metrics `operational` + real capacity + real `operational` watchdog). The honesty invariant
— metrics-status ↔ capacity coupling — is asserted in both directions. Live proof produced
real figures off this host (cpu 9.2%, mem 13.89/31.26 GiB, real disks) and a real watchdog
`operational`.

**Found & fixed at the gate:** a controller `404` (`NotFound()`) was emitting framework
RFC-9110 ProblemDetails instead of our `{error:{code,message}}` envelope — `[ApiController]`
auto-maps client-error results. Fixed with `SuppressMapClientErrors=true` so 4xx flow through
`UseStatusCodePages`. **Forward note:** when request bodies arrive (M3/M8), model-validation
`400`s will likewise bypass ProblemDetails and need routing through the envelope.

**Owed to the frontend at the gate:** the §6 divergences are not a clean backing-flip —
capacity is nullable, `info.intervalMs` (ms) replaces `interval_s` (s), `since`/`transport`
are omitted, and `/hosts/{id}` == the list shape (sensors/network/processes deferred). Agree
these before the store swap.
