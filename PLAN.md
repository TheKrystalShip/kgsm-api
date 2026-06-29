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

**Today:** M0 `partial`, **M1·a/M1·b `partial`**, **M2 `partial`**, **M3 `partial`**, **M4·a/M4·b `partial`**
and now **M5 `partial`** (backend built & self-validated; frontend gate pending). M5 = the append-only audit
log: kgsm events → audit rows via kgsm-lib's `IEventService`, the command path stamping actor+origin so the
engine echo carries provenance (no double-write), `GET /audit` (keyset) + the `audit` WS topic; SQLite via
`EnsureCreated` (no EF migration — dev authority). The skeleton (controllers + EF Core, **JIT**; §8),
the first leaf wiring — `GET /hosts` + `/hosts/{id}` scraped from kgsm-monitor with the §4·b
capability block — **the join `GET /servers` + `/servers/{id}`** (kgsm-lib domain +
run-state ⋈ monitor per-instance metrics, the honest `Server` DTO frozen in §6), and now **the
realtime `GET /api/v1/stream` WebSocket** (per-host topics pushed by gated pumps + an always-on
leaf health monitor; the capability set fixed at connect, status flipping on `/health` polls), and now
**the first write path `POST /servers/{id}/commands`** (gate → `202` + `job` → `jobs` WS → verify; verbs
`start`/`stop`/`restart`, `update` deferred) are
built, and now **the auth boundary `POST`-protecting it all** (M4·a — Discord per-host Model A, the
credential-independent half: stateless JWT bearer + hierarchical viewer/operator/admin tier policies +
`[Authorize]` on every prior endpoint, auth **on by default** with an explicit `KGSM_API_AUTH_DISABLED=1`
dev escape) are built. `scripts/smoke.sh` is **31/31** (8 M0 + 4 M1·a + 6 M1·b + 7 M2 + 3 M3 + 3 M4·a) and
**tests/Api.Tests is 30/30** (the 401/403/tier matrix + the callback/refresh/session flow, Discord seam
faked), proven both ways: degrade path
(no monitor → host metrics `down`/capacity `null`, every server `metrics:null`, stream falls silent) and happy path
(live/stub monitor → real host capacity, a real `operational` watchdog probe, the servers
join's present-branch by id, honest ticks streamed, and a full kill→restart degrade→recover cycle). The superseded .NET 9 attempt is
parked in `legacy/` for harvest (keystone O4). Pending: the live frontend handshake + venue.
The two read-side inputs it aggregates are `built` and ready: kgsm-lib 1.6.0 (domain +
run-state façade + `IWatchdogClient`) and kgsm-monitor (host + per-instance metrics over
`/run/kgsm-monitor/metrics.sock`), the latter now consumed through the shared
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
  as its own decision (§5, "Open" → `docs/metrics-history-plan.md`), not bundled into
  the early read milestones.
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
  - Project stands up; `/api/v1` base; `/health` (ops liveness; renamed from `/healthz` at M2); the standard error envelope
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

**M1·b — Servers (the join).**  ·  `partial` (backend built & self-validated 2026-06-14; frontend gate pending)
- **Goal:** join domain + status (kgsm-lib) with per-instance metrics (monitor).
- **Wires:** adds kgsm-lib (embedded, `GetAllStatuses(fast:true)` + `GetAll`); join on instance id
  (lib-side `instance name == dict key` is live-proven; the monitor-side `ServerMetrics.Id == instance
  name` is **by contract** — `Snapshot.cs`: "Id = stable instance name" — and stub-asserted, not yet
  observed from a live monitor row, see the §8 honesty boundary).
- **Scope:** `GET /servers`, `/servers/{id}`. **Honest DTO:** status tri-state from
  `Reading<T>` (`running` / `stopped` / `unknown`); metrics `cpuPctCore` (**preserved
  unit** — % of one core, can exceed 100), `memBytes`, nullable `io*`, `pids`, or **null**
  when the monitor is absent; every server carries `hostId` (this host).
- **Built (2026-06-14):** `ServerDto` (the frozen §6 shape), `ServerAggregator` (the join — roster
  from `GetAll`, run-state/version from `GetAllStatuses(fast:true)`, metrics by id from the monitor
  snapshot; the two blocking kgsm-lib spawns run on the thread pool concurrently with the async
  scrape), `ServersController`. kgsm-lib is **engine/base, not a leaf**: provisioned-by-default at
  the AUR-packaged `KGSM_API_KGSM_PATH` (`/usr/bin/kgsm`); an unconfigured engine degrades to an
  empty list + a one-time log (no §4·b "engine" capability). `IInstanceService` (transient) is
  resolved per-request from the provider. `blueprint` is the clean id (strips the unified-blueprint
  `.bp.yaml`, not just the last extension). Status/metrics are independent: a stopped server with no
  monitor row is `status:"stopped", metrics:null`, never inferred from the other.
- **Self-validated:** `scripts/smoke.sh` → **18/18** (8 M0 + 4 M1·a + 6 M1·b). Phase A (no monitor,
  live dev kgsm): honest DTO shape + every server `metrics:null` + the `{unknown}` 404 envelope —
  proves the domain read, the status mapping, and the null branch live. Phase B (embedded stub monitor
  serving a canned `Snapshot` with a per-server row keyed to the real instance): the **join's
  present-branch** — `cpuPctCore`>100 and a null `ioWriteBps` carried through verbatim, keyed by id —
  plus the host happy path made deterministic. Live domain read off this host: `factorio-test`
  (`native`, blueprint `factorio`, version `2.0.76`, `stopped`). The non-null join is **stub-proven**
  (no running instance to scrape on this box), the domain read + null branch are **live-proven**.
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

### M2 — Realtime: WebSocket per host  ·  `partial` (backend built & self-validated 2026-06-15; frontend gate pending)
- **Goal:** push the M1 data instead of polling (resolves keystone O2 → WebSocket).
- **Wires:** monitor tick → push; kgsm-lib status-change → push. *Reality:* neither source
  pushes, so the API **polls internally / pushes externally** — two gated background pumps
  (`MetricsPump` ~1s, `DomainPump` ~3s) plus the always-on `LeafHealthMonitor` (~2s) fan out
  through a per-host hub.
- **Scope:** `/api/v1/stream` (WebSocket, **unauthenticated until M4** — a pre-auth read surface);
  `{ topic, type, data }` envelope; subscribe/unsubscribe; topics `servers`,
  `servers/{id}/metrics`, `hosts/{id}/metrics`, `hosts/{id}/capabilities`; the §3·j resilience
  handshake (per-host reconnect/backoff, poll-fallback, re-hydrate on return).
- **Built:** the full M2 contract is frozen in §6. Raw ASP.NET Core WebSockets (not SignalR — the
  hand-rolled `{topic,type,data}` envelope is the contract); central `Realtime/StreamProtocol.cs`
  (no inline topic/type strings); coalesce-to-latest per-key backpressure (a slow client gets the
  latest, never an unbounded backlog; a stalled send is torn down → §3·j reconnect). Patch-only,
  no snapshot-on-subscribe (the client hydrates via REST). One shared `MetricsMapping` makes a WS
  tick byte-identical to the REST element it patches.
- **Capability model (refined here):** availability is driven by an **always-on `LeafHealthMonitor`**
  that polls each provisioned leaf's `/health` every ~2s (monitor/assistant HTTP `/health`; watchdog
  `IsReadyAsync` via kgsm-lib — the chokepoint), the single source feeding **both** the REST `GET /hosts`
  capability block and the `hosts/{id}/capabilities` stream. `provisioned` (the capability *set*) is fixed
  at connect and never flips; a leaf failing flips only `status` (operational→down→operational) with
  `provisioned:true` — degrade *and* recover gracefully, capability never "lost". `since` is now stamped
  (when the api observed the flip). This also **fixes** an M1·a honesty bend — status no longer inferred
  from `/metrics` frame-presence (a warming monitor is `operational` with null capacity).
- **Honesty:** monitor-down → metric topics go **silent** (never a replayed stale frame); the
  `hosts/{id}/capabilities` `down` flip explains the silence.
- **Self-validated:** `scripts/smoke.sh` → **25/25** (8 M0 + 4 M1·a + 6 M1·b + **7 M2**), incl. a
  stdlib RFC6455 client that subscribes, reads honest ticks, proves the `servers` topic stays quiet
  under the metric firehose, and (kill **then restart** the stub monitor mid-stream) proves the full
  **degrade→recover** capability lifecycle: down flip + tick silence, then operational flip + ticks
  resume, `provisioned:true` throughout. **Boundary:** `server.patch`/`server.removed` emission is
  code-path-only (roster static in smoke; see §8).
- **Depends:** M1 (same DTOs, now streamed).
- **Risk:** WebSocket lifecycle/backpressure and the message-envelope contract (the
  ASP.NET Core WebSocket middleware is bog-standard under JIT).
- **Frontend gate:** `realtimeStore` connects, applies patches, falls back to polling
  on drop and snaps back on reconnect.

### M3 — Commands: gate → job → verify  ·  `partial` (backend built & self-validated 2026-06-15; frontend gate pending)
- **Goal:** the first write path — lifecycle actions.
- **Wires:** **kgsm-watchdog leaf** (via kgsm-lib `ILifecycleService`: native→watchdog, container→Docker).
- **⚠ Trust-window assumption — CONFIRMED (user, 2026-06-15):** M3 mutates a real host a full
  milestone **before** M4 authenticates. Accepted **only** because the API is validated on a
  **trusted local network and is not exposed publicly until M4 lands**. (If that ever can't be
  guaranteed, pull M4 auth forward to gate M3 — it's a safety boundary, not a nit.)
- **Scope (first cut):** `POST /servers/{id}/commands { verb: start|stop|restart }` → `202` + `job`;
  the `jobs` WS topic (single `job.patch`, patch-only) for progress/completion; the **admissibility
  gate** (state guards now, permissions at M4); a **verify** re-check (`server.patch`) when the job
  settles. **`update` deferred** (user, 2026-06-15) — long-running + version-changing; the three fast
  run-state verbs ship first, `update` follows once the job-progress story is proven. Contract frozen
  in §6.
- **Depends:** M1/M2; M4 auth can be pulled forward to gate M3 if the trust window can't be guaranteed.
- **Risk:** real mutation — the gate, the one-in-flight-per-server guard, and (once M5 lands) the audit
  write must be right. Optimistic-UI reconciliation contract.
- **Self-validated:** `scripts/smoke.sh` → **28/28** (8 M0 + 4 M1·a + 6 M1·b + 7 M2 + **3 M3**): the
  gate/rejection contract (`400` unknown verb · `404` unknown server · `409` no-op-against-real-status),
  proven **without mutation** (the gate rejects before any verb runs). The happy path the stub smoke can't
  reach — `202` + job + `job.patch` lifecycle (`running→succeeded`) + verify `server.patch` + the in-flight
  `409` guard (6 concurrent POSTs → 1×`202` / 5×`409`) — was **live-validated on the trusted host**
  (2026-06-15), per the confirmed trust window. See §8 (incl. the watchdog-must-be-up finding).
- **Frontend gate:** confirm the trust window above; then action buttons → optimistic transitional
  state → `jobs`-topic tracking → reconcile to the authoritative status on `server.patch`.

### M4 — Auth: Discord per-host (Model A)  ·  `partial` (M4·a + M4·b backend built & live-validated 2026-06-15; frontend gate pending)
- **Goal:** the security boundary — make good on the trust window M3 ran under.
- **Wires:** Discord (external IdP, silent SSO) + this host's bot (role → tier).
- **Split like M1** — the one milestone with an unfabricable live dependency (a real Discord app +
  bot token + guild + role-map). The credential-*independent* half is fully buildable/self-validatable
  now; only the live OAuth round-trip gates on creds + the trusted host.

**M4·a — the credential-independent half.** · `partial` (built & self-validated 2026-06-15)
- **Bearer mechanism (the §5-open decision, RESOLVED):** **stateless JWT** (HMAC-SHA256; access ~15 min +
  refresh with an 8h absolute cap) — no session table, no user row (honors "no user row anywhere",
  keeps M5 as the first EF migration). The signing key derives from `KGSM_API_AUTH_SIGNING_KEY`
  (SHA-256 → 256-bit; ephemeral + loud warn if unset).
- **Built:** `Services/Auth/` — the **Discord seam** `IDiscordIdentityResolver` (everything that talks to
  discord.com behind one interface, so the whole 401/403/tier matrix is testable with a fake — the
  M3-style "exercise the contract without the live dependency" move); `SessionTokenService`
  (mint/validate, shared `TokenValidationParameters` with the JwtBearer pipeline); the hierarchical
  `TierRequirement`/handler (viewer⊆operator⊆admin); `DisabledAuthHandler` (the escape hatch — synthetic
  admin). `AuthController` (`/auth/discord/start` 302, `/auth/discord/callback`, `/auth/session/refresh`,
  `/auth/session`, `/auth/logout`). `[Authorize]` on Hosts/Servers (viewer) + the command `POST` (operator)
  + the `/stream` WS (viewer; the bearer rides `?access_token=` since a WS handshake can't set a header).
- **Auth is ON by default;** `KGSM_API_AUTH_DISABLED=1` is the explicit, loudly-logged dev escape hatch.
- **Honest failure modes (the security analog of never-fabricate-a-status):** Discord unreachable → `502`,
  never a default grant; `none`/not-in-guild → terminal `403`; a failed role lookup is never silently
  downgraded; a refresh token is never accepted as an access bearer.
- **Self-validated:** `scripts/smoke.sh` **31/31** (the +3 M4·a no-token sweep: protected → `401` envelope,
  `/health`+`/api/v1` open, login → `503` unconfigured) + **`tests/Api.Tests` 30/30** (xUnit +
  `WebApplicationFactory`, Discord seam faked: the 401/403/viewer-operator-admin matrix, none-tier/
  refresh-as-access/wrong-signature/garbage rejections, the WS `?access_token=` path, and the callback
  verdict ok/denied/invalid/upstream-error + refresh rotation + session snapshot).

**M4·b — the live OAuth round-trip.** · `built` (live-validated 2026-06-15 on the trusted host)
- The real `DiscordIdentityResolver` (code exchange → `/users/@me` → `GET /guilds/{guild}/members/{user}`
  with the **bot token** — the only path to roles, since the `identify guilds` scopes don't carry them)
  is now **live-validated**: a real Discord login resolved an in-guild member's 3 roles to `admin`, minted
  the bearer, and that bearer passed live tier-gating end-to-end (see §8). The login endpoints `503` only
  until the Discord app / bot token / guild / role-map are configured. **Shared external config, not a
  kgsm-bot process dependency** (keystone §4).
- **OAuth `state` CSRF round-trip (added here):** `/start` sets a one-time HttpOnly state cookie (the
  stateless double-submit nonce — no server store, honoring the no-session-table decision); `/callback`
  rejects a missing/mismatched state with `400 invalid_state` *before* any Discord exchange. `Secure`
  tracks the scheme (off on http loopback, on under https); `SameSite=Lax` so it rides Discord's
  top-level redirect back. Fake-tested (mismatch + no-cookie) and exercised on the live login.
- **Depends:** M0 (the auth-pipeline placeholder, now filled). Tier-gating of M3 commands is live.
- **Outstanding before `built` flips to the full M4:** the **frontend gate** only — the per-host session
  state machine end-to-end + tier-gated controls (the SPA, still `planned`). Op note: set a stable
  `KGSM_API_AUTH_SIGNING_KEY` on any real host (dev ran ephemeral — tokens die on restart).
- **Risk:** the security boundary itself.

### M5 — Audit log + SQLite (the event-persistence consumer)  ·  `partial` (backend built & self-validated 2026-06-15; frontend gate pending)  ←  *resolves keystone O3*
- **Goal:** the durable, append-only action record — **persistence downstream of the
  stateless engine**, exactly where O3 says it belongs (a consumer, never KGSM).
- **Wires:** **kgsm event socket** (kgsm-lib `IEventService` — the chokepoint, never a raw socket);
  the `Actor`/`Timestamp`/**`Origin`** enrichment shipped in kgsm-lib 1.6.0/**1.8.0** feeds the audit
  `actor`/`ts`/`origin`. (This milestone bumped the api's kgsm-lib ref **1.6.0 → 1.8.0**, also picking up
  1.7.0's watchdog `/health` switch.)
- **Built (2026-06-15):** `Data/AuditEntry` (the §3·d schema, `origin` nullable) + `AppDbContext`
  (table `audit`, unique id + the four `(col, rowid DESC)` scope indexes); `Contracts/AuditDto`
  (`AuditRecord`/`AuditPage` + the closed `action`/`severity`/`origin`/kind/provider vocabularies);
  `Services/Audit/` — `AuditMapping` (flat-actor `provider:name` → `{kind,name,provider}` parse with
  kind derived from provider, origin normalization, event→write, entity↔record), `AuditService` (the
  single serialized writer — own DI scope per write, `EnsureCreated`, publishes `audit.append`),
  `AuditQueries` (keyset page + filters), `KgsmAuditConsumer` (the `IHostedService` that subscribes to
  kgsm lifecycle events → audit rows; degrades gracefully if the engine/socket is absent);
  `AuditController` (`GET /audit`, viewer). Command-path **provenance stamping**: `ServersController`
  → `CommandRunner` → `ILifecycleService.Start/Stop/Restart(serverId, actor, origin)` (actor from the
  bearer, origin caller-declared) — **no double-write** (kgsm owns `server.*`; the API records the
  event echo). `AuthController` writes `auth.login`/`auth.logout` directly (no kgsm event).
- **Scope:** subscribe to kgsm lifecycle events → map to the closed dotted `action`
  vocabulary → append via EF Core to the audit table (`architecture.html §3·d` schema, keyset
  pagination on `rowid`) → `GET /audit` + `audit` WS topic (`audit.append`). M3 commands are audited
  via the event echo (provenance stamped, not double-written); M4 auth writes directly. **No EF
  migration** — the schema is `EnsureCreated` (greenfield/dev authority; **user directive 2026-06-15**:
  no migrations in dev, wipe the DB on a schema change). The M0 `Probe` table is **removed** (replaced
  by `AuditEntry`); `_dbcheck` is now a read round-trip (the append-only table must not be probe-written).
- **Self-validated:** `scripts/smoke.sh` → **33/33** (+2 M5: `GET /audit` empty `{data:[],nextCursor:null}`
  page + the cursor/limit/severity/serverId/actor filters; the no-token sweep now includes `/audit` → 401);
  **`tests/Api.Tests` → 59/59** (+the M5 `AuditMappingTests` actor round-trip / event-map / meta round-trip
  + `AuditTests` keyset order/pagination/filters, the viewer gate, the `auth.login` write end-to-end, and
  the `audit` WS `audit.append` delivery). Release build **0-warning**.
- **Depends:** M3 (commands to record), M4 (actor identity), event enrichment + the 1.8.0 lifecycle
  provenance hook (`built`).
- **Risk:** append-only immutability discipline; fidelity of the kgsm-event → action mapping (tested via
  the round-trip); the no-double-write provenance contract.
- **Coverage honesty (the live half, like M3's mutation happy path):** the **event-sourced append from a
  real kgsm event** is `tests/`-proven (mapping + the service/WS path with a seeded write) but the live
  socket round-trip (a real `kgsm start` → an `instance_started` event → an audit row with the stamped
  provenance) is a **trusted-host live-validate**, owed once exercised against the dev kgsm. It needs the
  api's `KGSM_API_KGSM_SOCKET` bound first **and** that path listed in kgsm's `config_event_socket_filenames`
  (the multi-listener model) — events emitted while the api isn't listening are not backfilled.
- **Frontend gate:** `auditStore` prepends on `audit.append`; filters map to indexed columns.

### M6 — Alerts (condition-mirror) + ports  ·  `partial`
- **Goal:** the needs-attention surface + the one-click firewall fix. **Split into three**
  (M6 is too large for one increment): **M6·0** (the internal kgsm-lib-bump + audit-consumer
  extension — done), **M6·b** (ports — contract frozen, below), **M6·a** (alerts — crash source
  built + self-validated 2026-06-16; contract proposed, sign-off + live crash-validate pending).

**M6·0 — kgsm-lib 1.13.0 + audit consumers.** · `built` (committed `14bd4f8` + live-validated 2026-06-16)
- Bumped the api's kgsm-lib ref **1.8.0 → 1.13.0** (picks up `IFirewallService`, the firewall
  port events, and the crash events) and extended `KgsmAuditConsumer`/`AuditMapping` with
  `server.crash` (watchdog `instance_crashed`→warn / `instance_failed`→danger, both `system`)
  and `network.ports.open`/`.close` (the CLI-path firewall echoes). Pure mappers + 4
  `RegisterHandler` wires; tests 67/67. Live round-trip proven on all four paths — which
  **discharged M5's owed socket round-trip**. No wire contract (internal). See §8.

**M6·b — Ports.** · `partial` (contract **frozen 2026-06-16**; backend built & self-validated; the full `open_ports` path **LIVE-VALIDATED end-to-end 2026-06-16 with ufw active** — `open:true` enforced, the direct audit row, the app-join, the `network.patch` WS delivery, and restore all proven (8/8); only the frontend gate remains. The **kgsm-firewall enforcement-state** follow-up is now BUILT + LIVE-VALIDATED 2026-06-16 (Firewall.Contracts 1.1.0 / kgsm-lib 1.14.0): an inactive ufw now reads `firewall:"inactive"` + all-`open:true` (reachable, not closed), both transitions proven wire→api — see §6 + §8)
- **Goal:** required-vs-open ports per server + the host open-ports grid + the one-click fix.
- **Wires:** **kgsm-firewall** via kgsm-lib `IFirewallService` (1.13.0 — **no bump**); required
  ports from `Instance.Ports` (already carried by the `GetAll` roster — `instances list
  --detailed --json`, confirmed live; no extra spawn).
- **Scope (3 deliverables):**
  1. `network` block on **`GET /servers/{id}` (detail only — the first place detail ≠ the list
     element)**: `required[]` (from `Instance.Ports`, ranges `Expand()`'d to per-port rows) ⋈
     per-row `open` (firewall rule present, via `ListOwnedAsync(instance)`).
  2. `network.openPorts[]` on **`GET /hosts/{id}` (detail only)**: the raw firewall listing
     (`ListOwnedAsync(null)` across all instances), `app` joined from the roster.
  3. `POST /servers/{id}/commands { verb: "open_ports" }` — **intent-only, no client port
     list**; server-derives the target from `Instance.Ports`; `EnsureOpenAsync` → re-probe
     verify; **direct** audit write (`network.ports.open` — no kgsm echo on the
     `IFirewallService` path, so no double-write).
- **Frozen decisions (user, 2026-06-16):** (1) **`reachable` reserved** — emits `null`, no
  upstream prober exists; the honest verdict is per-row `open` = host-firewall-rule-present
  (the rename-not-redefine call, like M1·b `cpu`→`cpuPctCore`); (2) the WS verify rides a
  **dedicated `servers/{id}/network` topic** (`network.patch`), so `server.patch` stays the
  frozen M1·b `Server`; (3) firewall liveness is a **block-level `firewall` status** only —
  `HostCapabilities` unchanged, NOT added to the 2s `LeafHealthMonitor` poll (it's
  socket-activated + idle-exits ~30s — a poll defeats it; probed on-demand). Full shape in §6.
- **Honest-unknown:** `required[]` is always present (domain truth, firewall-independent);
  `open`/`reachable` go **null** (never fabricated `false`) when the firewall can't answer;
  host `network` is **null** when unreachable, **`[]`** when Ok-but-empty (the
  `ListOwnedAsync` `Unknown`≠empty distinction preserved).
- **`open_ports` gate:** 404 unknown server; **no state no-op** (declarative/idempotent —
  always admissible); 409 if a command is already in flight (shared per-server slot).
- **actionId / alert↔audit bridge:** direct-write means the api owns **both** job and audit
  append, so it **can** correlate (the M5 "no jobId" limit was the event-echo path only) —
  `meta.jobId` on the audit row + `actionId` = the audit `evt_` id on `audit.append`; no new
  field on the generic `Job`.
- **Depends:** M5 (audit), M3 (the command path), M6·0 (the kgsm-lib bump + the ports vocab).
- **Op note (live validation):** the api process needs `kgsm`-group membership to reach
  `/run/kgsm-firewall/firewall.sock` (root:kgsm 0660) — the M6·0 constraint.
- **Frontend gate:** the per-server network card (renders `required[].open`; derives "all open"
  itself; shows reachability as "unknown") + the host open-ports grid + the open_ports flow.

**M6·a — Alerts (condition-mirror).** · `partial` (crash source built & self-validated + **LIVE-VALIDATED** 2026-06-16; contract PROPOSED — frontend sign-off pending. See §6 + §8.)
- **Goal:** the needs-attention surface.
- **Scope (built — crash source only, user-chosen 2026-06-16):** `GET /alerts?status=firing|resolved&since=24h`
  + the `alerts` WS (`alert.raise`/`resolve`/`retract`), read-only, in-memory (no EF table), viewer-gated.
  The one honestly-sourceable condition today is the watchdog's supervision state, polled via kgsm-lib
  `IWatchdogClient.ListAsync()` (the chokepoint): `restart-pending`→firing `warn`, `failed`→`escalated`
  `danger`. **Poll-as-authority** (the interval IS the raise debounce — §3·c "don't fire on a blip");
  **api-owned resolve probation** (30s dwell, no flap); **mirrored escalation**; **retract** on a vanished
  instance; 24h rear-view; rebuilds-on-restart; honest-unknown on a blind poll.
- **The bridge (and its limit):** `resolution.actionId` is set off a `server.start`/`server.restart` audit
  row (the hand-off) for an **operator/api** recovery AND for the watchdog's **autonomous** crash-restart
  (which emits `instance_restarted` system/system since kgsm-watchdog `d4b453f` → a `server.restart` row), so a
  pure auto-heal bridges too. The bridge is **episode-scoped** (`AlertEngine.BuildResolution` honors a stashed
  action only when its audit-row timestamp post-dates that crash's raise), so a dropped recovery event can never
  let a stale prior-episode action mislink a later crash; the boot-autostart (system-origin start) is audited
  but never bridged (`KgsmAuditConsumer.IsRecoveryAction`). A crash cleared by a **stop**, or whose own recovery
  event dropped, links to `null` — never fabricated.
- **Deferred (no honest source at M6·a — like M6·b's reserved `reachable`):** monitor CPU/RAM/disk thresholds
  (the `host-monitor`/`metrics` source — needs a dwell evaluator), leaf-down (already on the
  `capabilities.patch` axis — infrastructure, not a §3·c game-server condition), port-unreachable (no prober).
- **Depends:** M5 (the audit bridge), M6·0 (the watchdog crash events + `IWatchdogClient`).
- **Done:** the live watchdog-crash → alert validate (LIVE-VALIDATED 2026-06-16 — real crash→raise→resolve
  →`actionId:null` auto-heal; §8). **Owed:** the frontend sign-off on the §6 contract divergences.
- **Frontend gate:** `alertsStore` (the prototype-proven shape).

### M7 — Assistant turn relay  ·  `partial` (backend built & self-validated 2026-06-19, incl. a stub-assistant relay round-trip; real-model end-to-end + frontend gate pending)  ←  *resolves keystone O1*
- **Goal:** the AI surface — relay the assistant service's turn stream.
- **Wires:** **assistant leaf** (its own `/turn` SSE; proxy-verbatim is now *enabled*
  because the assistant emits the canonical typed vocabulary — keystone O1).
- **Strategy — RESOLVED (O1, 2026-06-19): near-verbatim relay; §5·a shaping pushed
  UPSTREAM into the assistant, NOT a re-wrap layer in the API.** The §5·a fields are the
  ones the assistant honestly owns (a tool-result card / a correlation id can't be
  synthesised from a relayed stream), and the assistant's *current* shapes don't yet match
  §5·a — so the fix lands in kgsm-llm and every surface (Discord/CLI/future) inherits it,
  not just the SPA. Spec: **`kgsm-llm/docs/m7-sse-5a-spec.md`** (the upstream work + staged
  card rollout). Blast radius is safe — only the assistant's tests + the SPA read the SSE
  (bot/CLI use the library in-process).
- **Fork — LOCKED (a) (2026-06-19): a confirmed assistant command executes through the
  API's M3 `POST /servers/{id}/commands`,** not a SPA→assistant `/confirm` call — one gated
  + audited + verified command path. Consequence: **`command.verified` is NOT a turn-stream
  event** (it rides M3's verify; the assistant's code + toolbox-plan §5·d agree); the SPA
  composes the verification block from the command path. The assistant's `/confirm` is kept
  (Discord/CLI + the fallback for verbs the API doesn't yet expose — see the spec §6 matrix).
- **Scope:** `POST /api/v1/assistant/turn` relays the assistant's (now §5·a-shaped) SSE
  (`text.delta`/`tool.start`/`tool.result`/`command.proposed`/`done`/`error`, + opt-in
  `thinking.delta`), capability-gated per host. `command.verified` is out of the relay (M3).
- **Auth — RESOLVED (trusted co-located relay, 2026-06-19):** the assistant's `/turn` requires its
  OWN Discord session (stateful, opaque — the API can't mint one), so the assistant gained a
  **relay-auth path** (kgsm-llm `64abac8`): a shared `Assistant:Relay:Secret` + forwarded Discord
  identity (`X-Relay-User`/`-Name`) → principal, mirroring its webhook-secret trust; authority is
  still derived from the bot, per-user memory key `web:<userId>` intact. The API forwards the
  verified caller's Discord id from its JWT + the shared secret (`KGSM_API_ASSISTANT_RELAY_SECRET`).
  This closes keystone O1's "OAuth-sharing settles at bring-up."
- **Built (2026-06-19):** `AssistantController` (`POST /api/v1/assistant/turn`, **viewer**-gated — a
  turn proposes but never executes); degrade-gracefully **capability gate** (assistant absent → 404,
  down → 503, reachable-but-rejecting → 502, all the `{error}` envelope, decided **before** the SSE
  commits); `AssistantClient.OpenTurnStreamAsync` (the relay client — `ResponseHeadersRead` so the
  long SSE body isn't `HttpClient.Timeout`-bound, forwards the secret + identity); near-verbatim
  body relay (`X-Accel-Buffering:no`, buffering disabled, `RequestAborted` tears the chain down →
  the assistant aborts Ollama generation). `ApiOptions.AssistantRelaySecret` config.
- **Self-validated:** `scripts/smoke.sh` → **42/42** — the gate checks (+2: assistant-absent → 404
  capability gate, blank prompt → 400; the no-token sweep now covers `POST assistant/turn` → 401)
  **AND a dedicated stub-assistant relay phase** (a fresh API pointed at a TCP stub that GATES on the
  relay secret + echoes the forwarded user): the API reaching `200` proves it forwarded the correct
  `X-Relay-Secret`, `X-Relay-User=dev` echoed proves identity forwarding, and the §5·a frames coming
  through verbatim prove the byte relay. Plus `tests/Api.Tests` → **109** (+4 `AssistantRelayTests`:
  no-token 401, none-tier 403, viewer+absent 404, blank-prompt 400); Release **0-warning**. The
  upstream §5·a Phase 1 is committed (kgsm-llm `bda373a`, suite 349 green).
- **Depends:** M4 (auth); the assistant service's **upstream §5·a pass** (`kgsm-llm/docs/m7-sse-5a-spec.md`,
  Phase 1) — **DONE** (committed `bda373a`).
- **Risk:** SSE relay/streaming correctness — now stub-proven end-to-end (the deterministic M2-stub
  pattern, not M3's non-deterministic mutation).
- **Op note (role-map consistency):** the turn is **viewer**-gated at the API, but the assistant derives
  `canPerformActions` from the bot role **independently** (its own `ActionRoleId`). Configure the API's
  operator role and the assistant's action role to the **same** Discord role — else a viewer could see
  `command.proposed` frames (inert under fork (a), since execution is the operator-gated M3 path, but
  confusing). The display name forwarded as `X-Relay-User-Name` is control-char-stripped at the boundary.
- **Coverage honesty:** the **full relay path** — identity + secret forwarding + byte-faithful
  streaming — is **stub-proven** (the smoke relay phase). The only piece still owed is a **real-model
  (Ollama) end-to-end** through a live assistant — a nicety, not the security-critical part (that's the
  header forwarding, now asserted).
- **Frontend gate:** the thread renderer against real SSE; the dock's per-host assistant
  picker honors the `assistant` capability.

### M8 — Install · library · cover art · settings/integrations  ·  `partial`
- **Goal:** the create operation + the config surfaces.
- **Wires:** kgsm-lib blueprints; RAWG (external, server-side, key off-browser); the
  Discord integration routing layer.
- **Scope:** `GET /library` (+ server-side cover resolution), `POST /servers`
  (`blueprint` + `name` honored, the rest accepted-but-inert per §3·h), the install
  job + `server.install` audit; `/settings`, `/me`, `/integrations/discord` (+ `/test`).
- **Depends:** M3 (jobs), M5 (audit).
- **Split** (M8 is several surfaces): **M8·a** (the library catalog read — built, below),
  then **M8·b** (install `POST /servers` + uninstall `DELETE /servers/{id}` — built, below),
  then the config surfaces (`/settings`, `/me`, `/integrations/discord`).
- **Frontend gate:** the install form (handle the "collected but inert" fields honestly)
  + settings/integration panels.

**M8·a — Library catalog (`GET /library`).** · `partial` (backend built & self-validated 2026-06-19, incl. a live 29-blueprint catalog read; frontend gate pending)
- **Goal:** the smallest real M8 increment — the installable-game catalog, a **pure blueprint scrape**
  (the catalog analog of M1·a's host scrape: no leaf join, no mutation, no fabricated field).
- **Wires:** kgsm-lib `IBlueprintService.ListDetailed()` (`blueprints list detailed --json`) — engine
  base, not a leaf (provisioned-by-default; an unconfigured engine degrades to an empty catalog +
  a one-time log, exactly like `ServerAggregator`). No monitor/firewall/RAWG dependency.
- **Scope:** `GET /api/v1/library?q=&category=` (viewer-gated). The honest `LibraryEntry`:
  `{ id, name, type, steamAppId?, clientSteamAppId?, isSteamAccountRequired, ports[{start,end,proto}],
  specs{maxPlayers?,minRamMb?,recommendedRamMb?,baseDiskMb?}, cover, rawgSlug }`. `q` filters by id/name
  (case-insensitive substring); `category` is **reserved/inert** (no honest game-genre source on a
  blueprint — never fabricate a taxonomy).
- **Honest realization (frozen in §6) — covers DEFERRED, the never-fabricate call:** **`cover` is
  RESERVED — always `null`** at M8·a. Cover-art resolution (RAWG, server-side, key off-browser — §3·i)
  is its own later increment because honesty constrains it: resolve **only from an exact key**
  (`SteamAppId` → Steam CDN), never a fuzzy `DisplayName`→RAWG match (that mis-attributes the *wrong*
  game's art — fabrication-by-misattribution, the sin that scrapped the old api). `rawgSlug` is likewise
  reserved-`null` (no curated slug on a blueprint yet — `[kgsm-unified-blueprints]` metadata curation is
  deferred). `name` falls back to the blueprint `id` when uncurated (every blueprint's `Metadata` is
  null today, so `name == id`), never a guessed display name. `steamAppId`/`clientSteamAppId` are
  honest `null` for a non-Steam blueprint (NOT the `Server` DTO's `"0"` sentinel — a deliberate,
  frozen choice for this new surface). `specs` keys are always present but every value is `null` today
  (uncurated upstream) — a `null` spec is "unknown", never a fabricated 0.
- **Upstream piece — the canonical-port-format migration extended to blueprints (kgsm + kgsm-lib 1.17.0):**
  the blueprint surface emitted `Ports` only as the legacy UFW string (`"26900:26903/tcp|26900:26903/udp"`)
  — unlike `instances info --json`, structured since the 1.10.0 port-format cutover. The **root-cause fix**
  (not a C# re-parse): **kgsm now emits the canonical `[{start,end,protocol}]` array on `blueprints … --json`**
  (reusing the existing `__ufw_ports_to_json` chokepoint helper — native ports from the UFW spec, container
  ports derived into the same spec, empty/malformed → `[]`), and **`Blueprint.Ports` becomes `List<PortMapping>`**
  (kgsm-lib **1.17.0**, BREAKING) — one ecosystem port type everywhere, no consumer parses a string; the
  catalog projects it directly. (An initial cut parsed the string in kgsm-lib via a new `FromUfwSpec`; the
  user correctly flagged the upstream fix as the right place, so that parser was **removed** — §8.)
- **Built (2026-06-19):** `Contracts/LibraryDto.cs` (the frozen shape above), `Services/Library/LibraryAggregator.cs`
  (resolve `IBlueprintService` per-request, degrade-to-empty + log-once, map + sort by id + `q` filter),
  `Controllers/LibraryController.cs` (`GET /library`, viewer). The api's kgsm-lib ref bumped to 1.17.0.
- **Self-validated:** `scripts/smoke.sh` → **44/44** (+2 M8·a: a **live read of the real 29-blueprint
  dev catalog** that proves the **whole bash→lib→api chain** — the frozen key set, structured ports straight
  from kgsm (no C# parse), steam null-honesty both ways, and reserved cover/rawgSlug; the `q` filter —
  `factorio`→matches, a no-match→`[]`; the no-token sweep now covers `/library` → 401). **`tests/Api.Tests`
  → 120** (+11 `LibraryTests`: the Blueprint→`LibraryEntry` mapping honesty — null-not-zero steam, id-fallback
  name, reserved cover/rawgSlug, structured-port projection, curated-metadata override, the q filter, id
  ordering, and the engine-unconfigured / read-failure degrade-to-empty; + the viewer/none/no-token gate
  through the real pipeline). kgsm-lib **green** (`Blueprint`/`PortMapping` suites 36/36). kgsm-bot rebuilt
  clean against the breaking `Blueprint.Ports` (the `KgsmBlueprintService` field-copy is List=List). Release **0-warning**.
- **Build hygiene (environmental, not M8·a):** a freshly-published advisory (GHSA-2m69-gcr7-jv3q) on the
  SQLite **native lib** pulled transitively by EF Core (`SQLitePCLRaw.lib.e_sqlite3` 2.1.11, the latest —
  no patched build exists yet) tripped `TreatWarningsAsErrors`. Cleared with a **scoped** `NuGetAuditSuppress`
  for that one advisory (NOT a blanket audit-off) in both csprojs, documented inline + **to be deleted the
  moment a patched bundle ships**. Exposure is minimal (SQLite holds only this host's local append-only
  audit log on a trusted single host).
- **Depends:** M1·b (the kgsm-lib engine-base wiring + the optional-resolve pattern).
- **Frontend gate:** `libraryStore` swaps mock → real; the install picker's game grid renders from real
  blueprints. **Not a clean backing-flip:** `cover` is reserved-`null` (the grid falls back to the themed
  `art` gradient until the RAWG increment), `specs` are all-null today, and `name == id` until metadata
  curation lands — agree these before the store swap.

**M8·b — Install + uninstall (`POST /servers` / `DELETE /servers/{id}`).** · `partial` (backend built, self-validated, & **LIVE-VALIDATED GREEN end-to-end 2026-06-19** — real install + uninstall round-trip; surfaced + fixed an upstream `kgsm uninstall` interactive-only gap (kgsm-lib 1.18.0); committed to `main` (not pushed); frontend gate pending)
- **Goal:** the panel's one CREATE operation + its delete — the async install/uninstall write path.
- **Wires:** kgsm-lib `IInstanceService.Install`/`Uninstall`/`GenerateId` (engine base — all already carry the
  `(actor, origin)` provenance params). **No upstream work needed** (verified end-to-end): kgsm already emits
  `instance-installed`/`instance-uninstalled` with provenance off the **global** event chokepoint
  (`events.sh` reads `KGSM_EVENT_ACTOR`/`_ORIGIN` for every event), kgsm-lib already types the event data, and
  the M5 audit consumer already maps both to `server.install`/`server.uninstall`. M8·b is a pure api wiring
  increment.
- **Scope:** `POST /servers` (install) + `DELETE /servers/{id}` (uninstall), both **operator-gated**, both
  async → `202` + a `job` (reusing the M3 `JobRegistry`/`CommandRunner` — one job model, one in-flight slot,
  one verify discipline). `install`/`uninstall` are NEW `job.Verb`s but deliberately **NOT** in the
  `POST /servers/{id}/commands` `IsKnown` set (one creates a server, one targets the collection → dedicated
  endpoints). Install honors **`blueprint`** (required) + **`name`** + **`origin`**; the rest of the §3·h form
  is **accepted-but-inert** (additive-only). The backend assigns the id via kgsm `generate-id` (the §3·h "id is
  the backend's to assign"), passed as the install `--name` (kgsm echoes an already-unique name **verbatim** →
  `job.serverId` == the new instance == the audit/verify key — `install.sh` re-runs the same idempotent
  generate-id internally). **Echo-path audit, NO double-write** (the lifecycle case, NOT open_ports' direct
  write): the command stamps actor+origin, kgsm emits the event, the M5 consumer writes the row. **Verify:**
  install → `server.patch` (the new server appears); uninstall → `server.removed` tombstone (once it leaves the
  roster).
- **Gate:** install — `400` missing blueprint / unusable blueprint-or-name (generate-id rejects, honest detail)
  / bad origin; `409` an install already in flight for the resolved name; `503` engine unprovisioned. uninstall —
  `404` unknown server (roster authority); `409` in-flight; `503` unprovisioned. No state no-op (install always
  creates, uninstall always removes; the engine owns subtler failures → job `failed` + its real error).
- **Built (2026-06-19):** `InstallRequest` DTO + `CommandVerb.Install/Uninstall`; `CommandRunner` install/
  uninstall branches + `PublishServerRemovedAsync` verify (reusing the `ServerRemoved` tombstone); `ServersController`
  `POST`/`DELETE` (+ a shared `TryResolveOrigin`). Release **0-warning**.
- **Self-validated:** `scripts/smoke.sh` → **47/47** (+3 M8·b gate checks: no-blueprint `400`, unknown-blueprint
  `400` against live kgsm's generate-id, unknown-server DELETE `404`; + `POST`/`DELETE /servers` added to the
  no-token `401` sweep — all gate/rejection, **NO mutation**, exactly like M3) + **`tests/Api.Tests` 135** (+15:
  the full gate matrix through the real pipeline with a faked engine — `400`/`404`/`503`/`401`/`403`, the `202` +
  the install/uninstall `job` shape, the **no-double-write** proof (a completed install leaves `/audit` empty),
  and — advisor-caught — the **model-validation `{error}` envelope** on a malformed/type-mismatched body (the
  first typed-body endpoint; see below). The mutation happy path (a real install → `instance_installed` → `server.install` row → `server.patch`;
  a real uninstall → `server.removed`) is **LIVE-VALIDATED GREEN 2026-06-19** on `hotrod` (real install + uninstall
  round-trip; the run surfaced + fixed an upstream `kgsm uninstall` interactive-only gap — see §8).
- **Depends:** M3 (the job/runner/verify), M5 (the audit echo + provenance stamping), M8·a (the catalog the
  install picker draws from).
- **Frontend gate:** the install form (handle the "collected but inert" fields honestly) + the uninstall action.
  **Honesty note for the gate:** `name` is honored as the **instance name** (kgsm validates it as an id and
  falls back to an auto-generated `blueprint-suffix` if it isn't a usable unique name) — a true free-text
  *display* name is deferred upstream (blueprint metadata curation), not silently dropped.

**M8·c — Config surfaces (`/me` · `/settings` · `/integrations/{discord,slack}`).** · `partial` (`/me` built 2026-06-19; `/integrations` **Increments A + B + C built & self-validated 2026-06-19** — config + a real test-send, the always-on delivery worker, and **Slack as a second provider** validating the webhook-family abstraction (live-validate with real webhooks owed); `/settings` NOT started)
- **Goal:** the panel's per-host config/identity reads — the last M8 surfaces. Split from the install/library
  write+catalog work because they're a different shape (identity + preferences + a connected integration).
- **`GET /me` — DONE (built & self-validated 2026-06-19).** The caller's identity + tier + scopes, a pure
  projection of the session bearer's claims (no engine/leaf/DB touch). `MeResponse { user: SessionUser{ id,
  username, display, avatarUrl? }, tier, scopes[] }`, gated `[Authorize]` (any authenticated caller — so a
  `none`-tier caller can read its own identity, not just be 403'd). **Honest realization (frozen in §6):**
  **read-only** — the surface table's GET+**PATCH** "Profile (display name, handle, **density**)" needs a
  per-panel preference store that is **deliberately not built** (the statelessness trade), so PATCH + density
  are deferred, never faked; the profile is the **login-time snapshot** (no retained Discord token to re-fetch,
  the §3·f divergence); the honest **delta over `/auth/session`** is the **`tier`** the SPA gates on. `MeController`
  + `MeResponse`; smoke **48/48** (+1 wire-shape under the auth-disabled synthetic admin; `/me` folded into the
  no-bearer 401 sweep), tests **143** (+8 `MeTests`). Release 0-warning.
- **`/settings` — NOT started.** Needs its **honest backing scoped first**: what is genuinely settable per host
  with a real source (the §3·d "Assistant endpoint & general preferences"). The assistant endpoint URL is a real
  config value; "general preferences" hit the **same no-preference-store wall** as `/me`'s PATCH half. Scope the
  honest, persistable subset before building — don't ship a settings surface backed by a store that doesn't exist.
- **`/integrations/discord` — a provider-agnostic notification subsystem (§3·e). DECIDED + Increment A built.**
  §3·e is a **notification-routing integration** (NOT an endpoint wire — the M4 `ApiOptions.Discord*` is **auth
  role-resolution only**; a grep of `src/` confirmed zero notification backing, the only EF entity was `AuditEntry`):
  a stored **webhook secret** (masked on read, write-on-PATCH), an **event-routing config** (`events[]` of
  `{id,enabled,cadence,ping}` over a server-defined catalog), and a `POST /test` that really posts. **Two decisions
  (the user's call, 2026-06-19):** (1) **kgsm-api owns it**, wired **behind a provider abstraction** so Slack/
  Telegram follow (§3·e's `/integrations/{provider}`); (2) **COEXIST with `kgsm-bot`** — kgsm-bot already posts
  online/offline/uninstalled to *per-instance channels* via the bot gateway, so the API posts to its **own
  configured webhook (one ops channel)** and kgsm-bot is left unchanged (no double-post unless both are aimed at one
  channel — documented caveat). **Scope guard:** one-way webhook delivery only — the §3·e two-way control **bot** +
  slash-commands are **out of scope** (kgsm-bot's interactive territory), so the Discord view's `bot` is honestly
  `null`. Sliced into 3 increments:
  - **Increment A — BUILT & self-validated 2026-06-19** (contract + config + a real test-send; **no** delivery
    worker). `IntegrationEntity` (the first non-`AuditEntry` table) + `IntegrationStore` (the `AuditService`
    scope-per-op + write-gate pattern); the thin `INotificationProvider` seam + `DiscordNotificationProvider`
    (webhook POST, typed `HttpClient`) + the server-defined `NotificationCatalog`; `IntegrationsController`
    (**admin-gated** GET list / GET `{provider}` / sparse PATCH / POST `{provider}/test`). **Honest realization
    (frozen in §6):** `bot:null`; catalog lists only **deliverable** events (`online·offline·crash·update·installed·
    backup`) — `resource`/`join` omitted (no honest source); webhook secret **masked on read** (hint), write-only on
    PATCH, never echoed; `cadence` accepts `every|once|digest` but enforcement is incremental (`every`=B, `once`/
    `digest`=C) — accepted-but-inert (the M8·b reserved-field pattern); `/test` posts for real or fails honestly
    (409 unconfigured · 502 delivery-failed · 202 ok). The webhook URL **is** the secret, so the masking holds on
    the **log channel** too: the typed `HttpClient` is registered `.RemoveAllLoggers()` (the factory's default
    handler logs `POST {uri}` at Information — it would leak the token) — advisor-caught, pinned by a regression
    test that captures logs. Release 0-warning; smoke **52/52** (+4); tests **166** (+23:
    11 provider unit incl. a faked-HTTP send, 12 API incl. the masked-secret round-trip, the admin gate, and the
    no-token-in-logs guard).
  - **Increment B — the delivery worker — BUILT & self-validated 2026-06-19** (live-validate with a real webhook
    still owed — smoke must never post to real Discord). An in-process `INotificationBus` tapped by
    `AuditService.AppendAsync` (the **always-on** path — every kgsm event is already an audit row, so it sidesteps
    the subscriber-gated `StreamHub`; **zero** new event-socket consumers, and the single-writer/no-double-write
    audit log means one action → exactly one notification). The bus is a bounded `Channel` (`DropOldest` — a stale
    `online` is shed before a fresh `crash`; every drop is logged). A `NotificationDeliveryWorker` (the always-on
    hosted-service shape) drains it, scope-per-event (the typed-client providers are disposable), and routes to
    each enabled provider whose per-event rule is enabled at **`every`** cadence. `INotificationProvider` gained
    `SendAsync`; `DiscordNotificationProvider` formats by action (🟢 online / 🔄 restarted / ⚪ offline / 🔴 crash /
    ⬆️ update / 📦 install / 💾 backup) with an optional ops-role ping (`Settings["pingRoleId"]` + `allowed_mentions`
    scoped to that one role — which also suppresses an accidental `@everyone`). **Honest realizations (advisor-vetted):**
    (a) **`server.restart` → `online`** too (a completed restart = back up), so the watchdog's autonomous crash-restart
    delivers the "back online" that pairs with its crash, not a silent gap; (b) **anti-spam suppression** — a
    per-`(provider,server,catalog)` 60s window coalesces a crash-loop's repeats (the first fires, repeats within the
    window are skipped + logged) so B can't self-DoS the webhook (Discord rate-limits ~30/min) exactly when a server
    is dying; a **mass reboot** (N servers → N distinct keys) is **not** suppressed — bounded by the host's server
    count, accepted (heavier shaping is C); (c) **`once`/`digest` deliver ZERO in B** (not fewer) — the skip is logged
    at Information ("set 'every' to receive it") so a mis-set cadence is never a silent black hole. Crash via the
    `server.crash` row (every crash, modulo the suppression window); a finer `AlertEngine`-style debounce is a C
    refinement. Release 0-warning; tests **190** (+24: the action→catalog map, the provider `SendAsync`
    format/ping/honest-failure, and a deterministic end-to-end — an audit row appended through `AuditService` reaches
    a recording webhook, with the rule-disabled / once-cadence / suppression gates proven via a barrier event, no
    sleeps); smoke unchanged **52/52** (B adds no HTTP endpoint — it's a background worker, proven by the e2e tests).
  - **Increment C — the second provider (Slack) — BUILT & self-validated 2026-06-19** (the user's call: Slack
    over Telegram; `once`/`digest` cadence **deferred**, `resource`/`join` still have **no honest source** → out).
    The honest validation of the abstraction: extract an abstract **`WebhookNotificationProvider`** base holding
    the genuinely-shared logic (the honest `PostAsync`, the `Test`/`Send` orchestration, the catalog⋈rules
    `EventViews` overlay, and a `MaskWebhook(url, marker)` hint that reproduces Discord's exact committed hint),
    refactor Discord onto it (behavior identical — guarded by its 23 + e2e tests), and add
    **`SlackNotificationProvider`** (`hooks.slack.com`/`services/` validation, mrkdwn `*bold*`, `{text}` payload,
    optional `Settings["pingSubteamId"]` → `<!subteam^id>`, **`&<>` escaped** in dynamic text — the Slack analog
    of Discord's allowed_mentions care) + `SlackIntegrationView` (**no `bot` block** — Slack incoming webhooks
    have no Discord-style control bot; inventing one would be dishonest). Registered the same way
    (`AddHttpClient<INotificationProvider, SlackNotificationProvider>().RemoveAllLoggers()`); the worker/catalog/
    controller were **already provider-agnostic** → picked up with no change there. **Honest framing (advisor):**
    the base validates the **webhook-secret-URL family** (Discord, Slack); **Telegram** (a bot token + a fixed
    endpoint + a `chat_id` — the secret is NOT the URL) would implement `INotificationProvider` directly, so it is
    "the next real test of the interface", not covered by this base. Release 0-warning; tests **207** (+17:
    `SlackProviderTests` mask/validate/format/escape/ping/honest-failure with a recording handler;
    `SlackApiTests` the list-shows-both, the no-`bot` view, the admin gate, the masked PATCH round-trip); smoke
    **54/54** (+2: `/integrations/slack` shape + masked round-trip; the list now asserts **both** providers).
    Live-validate with a real Slack webhook **owed**.
  - **Increment C.2 — cadence refinements (LATER, deferred by the user).** `once` (dedup/episode-reset state) +
    `digest` (timer/accumulation) cadence — they need real semantics decisions (what `once` resets on; the digest
    interval/persistence), so they stay **accepted-but-deliver-ZERO-and-logged** until a focused follow-up.
- **`/settings` — NOT started.** Needs its **honest backing scoped first**: what is genuinely settable per host
  with a real source (the §3·d "Assistant endpoint & general preferences"). The assistant endpoint URL is a real
  config value; "general preferences" hit the **same no-preference-store wall** as `/me`'s PATCH half. Scope the
  honest, persistable subset before building — don't ship a settings surface backed by a store that doesn't exist.
- **Depends:** M4 (the JWT identity `/me` projects); `/integrations` Increment B additionally taps M5's audit flow.
- **Frontend gate:** the `/me` profile chip (read-only — no edit affordance until the preference store lands); the
  Discord integration panel (the masked webhook + the event-routing grid — note `cadence` once/digest are
  accepted-but-inert until Increment C); the settings panel waits on `/settings`.

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
- ~~**Bearer mechanism** (EF `sessions` table vs JWT)~~ — **RESOLVED at M4·a: stateless JWT**
  (HMAC; access ~15 min + refresh with an 8h cap). No session table, no user row — honors the §3·f
  "no user row anywhere" doctrine and keeps M5 as the first EF migration. Trade: no instant
  server-side revocation, bounded by the short access TTL.
- ~~**Metrics-history store** (for `GET /servers/{id}/metrics?range=…`)~~ — **RESOLVED
  2026-06-24, decisions LOCKED → milestone M9, plan in `docs/metrics-history-plan.md`.**
  An **API-owned RRD-style tiered store**, fed off the existing `MetricsPump` (no second
  scrape); **not** the monitor (stays latest-only), **not** an external TSDB. **SQLite is
  the durable system of record** (history must survive restart — user), a dedicated
  `metrics.db`, narrow/long schema, two retention tiers (**24 h @ 15 s raw → 30 d @ 5 min
  rolled-up**), optional in-memory hot ring on top. 5 increments (write path → rollup/prune
  → read endpoint → hot ring → frontend gate); see that doc's §0 progress ledger. Build not
  started.
- ~~**Proxy-verbatim vs re-wrap** for the assistant SSE (O1)~~ — **RESOLVED at M7 (2026-06-19):
  near-verbatim relay; §5·a shaping pushed UPSTREAM into the assistant** (`kgsm-llm/docs/m7-sse-5a-spec.md`),
  and the **confirmed-command fork LOCKED (a)** (execute via the API's M3 path; `command.verified`
  stays off the turn stream). See M7.

---

## 6 · Cross-team contract registry (the shapes both teams agree)

Frozen here as each milestone's gate is reached; the authority is `architecture.html`
for the external surface and this doc for the backend's honest realization of it.

| Contract | Milestone | Owner / notes |
|---|---|---|
| Error envelope `{ error:{ code, message, details? } }` | M0 | `architecture.html §6` — **frozen, self-validated (500+404); browser fetch pending** |
| Base path `/api/v1` (path-versioned, additive-only) | M0 | `architecture.html §6` — **frozen** |
| JSON conventions: camelCase · ISO-8601 UTC `Z` · opaque ids | M0 | `architecture.html §6` — **frozen; camelCase + `Z` via shared JSON options (MVC + HTTP), verified** |
| `Server` DTO (honest realization) | **M1·b** | `architecture.html §3` — **frozen 2026-06-14.** `{ id, name, blueprint, status, version?, runtime, hostId, metrics? }`; `metrics:{ cpuPctCore, memBytes, ioReadBps?, ioWriteBps?, pids }` or **`null`**. Stable keys, explicit nulls. **Divergences from the §3 example (the negotiated honest-vs-aspirational contract):** `status` is `running\|stopped\|unknown` (from `Reading<InstanceRuntimeStatus>`), NOT `online\|offline\|updating\|crashed\|installing` (transitional states need the M3 job tracker + crash detection); `metrics.cpuPctCore` is **% of one core (can exceed 100)**, not `cpu` 0–100; `memBytes` replaces `ram{used,max}` (no honest memory limit); **omitted as unsourceable**: `players`, `ip`, `ram.max`, `updatedAt` (no state-change tracking until M2), and the curated `game` display name (we emit the real `blueprint` id — metadata curation deferred, never guessed). Join key: instance id (`monitor ServerMetrics.Id` == kgsm instance name == lib dict key). `/servers/{id}` == the list element shape (full detail later). **ADDITIVE (Monitor.Contracts 1.2.0):** `metrics.diskBytes?` — per-instance on-disk footprint (bytes), nullable, passed through 1:1 (slow-cadence working-dir walk). **ADDITIVE 2026-06-29 (Monitor.Contracts 1.3.0 — per-server NETWORK):** `metrics.rxBps?` / `metrics.txBps?` — per-instance network receive/transmit rate in **bytes/sec**, **nullable**. Now honestly sourced: the monitor's passive eBPF `cgroup/skb` byte meter on the instance cgroup (per `per-server-network-plan.md`) — this **reverses** the prior "per-server network has no honest source / deliberately not emitted" note. `null` (never `0`) when not measurable — meter not set up, `cap_bpf` absent, or a container instance not under `kgsm.slice`; same nullable-passthrough contract as `ioReadBps`. Purely additive (within `/api/v1`), no break; rides both `GET /servers/{id}.metrics` and the `servers/{id}/metrics` WS tick via the single `MetricsMapping.ToServerMetrics` (byte-identical). |
| `Host` DTO + capacity (`cpuPct`/`mem`/`disks`) | M1·a | `architecture.html §4·a` — **frozen 2026-06-14.** `{ id, label, status:"online", cpuPct, mem{used,total} GiB, disks[]{mount,used,total} GiB, capabilities }`. **Divergence (record for the gate):** capacity (`cpuPct`/`mem`/`disks`) is **nullable** — `null` when metrics ≠ operational (the §4·a example always shows numbers). `/hosts/{id}` currently == the list shape; §244's sensors/network/processes are deferred. |
| Capability record `{ provisioned, status, since?, message?, info? }` | M1·a · **refined M2** | `architecture.html §4·b` — **frozen 2026-06-14, refined 2026-06-15.** **Two independent axes, never conflated:** `provisioned` (bool) is the **fixed** "what leaves this host has" — resolved once from config, the one-time set the frontend negotiates at connect; it **never flips at runtime**. `status` ∈ `operational\|degraded\|down\|unknown` is the **live availability**, driven by frequently polling each leaf's health (M2 `LeafHealthMonitor`, ~2s) — monitor/assistant `GET /health`, watchdog `IsReadyAsync` via kgsm-lib. A leaf failing flips `status` (operational→down→operational), **never** `provisioned`: the capability is "temporarily unavailable, still there", never "lost" — `provisioned:true, status:down` IS the down notification (we never invent a softer status nor suppress the flip). `provisioned:false` → client-derived `absent`. `since` **now emitted** (M2): the timestamp this api *observed* the flip (not an authoritative leaf-change time). `degraded` reserved (a restarting leaf is `down`); cold (pre-first-poll) reads as `unknown`. **Divergences:** `info.intervalMs` (camelCase, ms) replaces the example's `info.interval_s`; `transport` **omitted** (REST + WS, not `"sse"`). |
| Monitor `/metrics` wire shape (`Snapshot` graph) | M1·a | **shared package** `TheKrystalShip.KGSM.Monitor.Contracts` — the DTO graph + source-gen camelCase JSON, built in kgsm-monitor and consumed here so the contract is solid at build time. **Drift rule:** any contract change MUST bump the package `Version` and the api's `<PackageReference>` (a same-version repack is silently served stale from the NuGet cache). |
| WS stream envelope + topic/type vocabulary | **M2** | `architecture.html §3·b/§3·j` — **frozen 2026-06-15.** Endpoint `GET /api/v1/stream` (WebSocket; **unauthenticated until M4** — a pre-auth *read* surface, less severe than M3's mutation but flagged). **Inbound:** `{ type: "subscribe"\|"unsubscribe", topics: [...] }`; unknown command type ignored; unknown/future topics accepted silently (forward-compat for `jobs` M3 / `audit` M5 / `alerts` / `console`); **no ack or error-frame protocol yet.** **Outbound:** `{ topic, type, data }`, patch-only (the client `hydrate(REST) + applyPatch(WS)`; **no snapshot on subscribe** — §3·j re-hydrates via REST on (re)connect). **Topics (M1-backable subset):** `servers` · `servers/{id}/metrics` · `hosts/{id}/metrics` · `hosts/{id}/capabilities`. **Message types** (only `server.patch` is doc-given; the rest are ours, negotiated like the M1·b DTO — all centralized in `Realtime/StreamProtocol.cs`, never inline strings): `server.patch` (data = the **frozen M1·b `Server`**, NOT the §3·b example's `{status:"online", players}`; carries the full element incl. a **point-in-time `metrics` block that may lag** the dedicated `servers/{id}/metrics` tick — merge by id), `server.removed` (`{ id }` tombstone), `metrics.tick` (`ServerMetricsDto`), `host.metrics` (`HostMetricsDto` = the capacity portion of the `Host` view; `net`/`temp` omitted, never fabricated), `capabilities.patch` (`HostCapabilities`). **`servers` carries status/roster only — NOT the 1s metric firehose** (resource ticks live on `servers/{id}/metrics`; a deliberate divergence from §3·b's "resource deltas" wording, smoke-proven the `servers` topic stays quiet under ticking metrics). **Honesty:** monitor-down → metric topics go **silent** (never a replayed stale frame) and `hosts/{id}/capabilities` flips metrics `down` — that flip is what explains the silence. **Capability availability is driven by the always-on `LeafHealthMonitor`** (frequent `/health` polls, the single source feeding both this stream and the REST `GET /hosts`), which stamps `since` on each flip — see the Capability-record row. The `capabilities.patch` keeps `provisioned:true` through a down→up cycle: degrade **and** recover gracefully, capability never "lost". |
| Command verbs + `job` shape + `command.verified` | **M3** | `architecture.html §5·d` — **frozen 2026-06-15.** **Endpoint** `POST /servers/{id}/commands` body `{ verb }` → `202` + `{ job }`; closed, server-defined verb set **`start`·`stop`·`restart`** (**`update` deferred** from the first cut — long-running + version-changing, settles on a version re-check not a run-state one). **Errors:** unknown/missing verb → `400 bad_request`; unknown server → `404 not_found`; an obvious no-op against the real status (start-when-running / stop-when-stopped) or a command already in flight for that server → `409 conflict`. **`job`** = `{ id, serverId, verb, state, createdAt, settledAt?, error? }` (opaque `job_…` id; ISO-8601 UTC `Z` times; `error` set only on `failed`, the engine's real detail — never a fabricated success). **Divergence (honest-vs-aspirational, the same negotiated call as the M1·b DTO):** `job.state` is the **job's own** lifecycle `queued→running→succeeded\|failed`, NOT the §5·d example's server-shaped `state:"running"` — the affected server's authoritative/optimistic status rides the `servers` topic via `server.patch`, and the client derives the optimistic display from the verb (the same topic-separation discipline as the metric topics). **WS:** the `jobs` topic carries a single **`job.patch`** (the full `job` on every transition, coalesced by job id — patch-only, exactly like `server.patch`). **Gate (state guards):** minimal/honest — only the obvious no-ops; the engine (kgsm→watchdog/Docker) owns everything subtler, surfacing an impossible transition as the job's `failed` + its error (the API never fabricates admissibility kgsm does not enforce; `unknown` status never blocks). **Verify** (the §5·d `command.verified` for the direct write path): on settle, a fresh run-state read → an explicit `server.patch`. **Permissions** gate at M4; jobs are **in-memory** (SQLite + audit at M5). The propose/confirm half of §5·d (`command.proposed` over SSE) is the assistant flow (M7), not M3. |
| Auth session + tiers + 401/403/login_required | **M4·a** | `architecture.html §3·f` — **frozen 2026-06-15 (M4·a).** **Bearer = stateless JWT** (HMAC; access ~15 min + refresh 8h cap; no session table). Endpoints `/auth/discord/start` (302→authorize), `/auth/discord/callback` (`{ verdict:"ok"\|"denied", tier, token, refresh?, userId }`), `/auth/session/refresh` (refresh bearer → `{ token }`), `/auth/session` (`{ user:{ id, username, display, avatarUrl? }, scopes }` or `401`), `/auth/logout` (`204`). Tiers `admin·operator·viewer·none` resolved from the guild role via the **bot token** (`GET /guilds/{guild}/members/{user}` — the only path to roles; the `identify guilds` user scopes don't carry them). `401` (no/invalid/expired bearer) recoverable; `403` (`none`/insufficient tier) terminal. **Divergences (the negotiated honest-vs-aspirational call, like the M1·b DTO / M3 job.state):** (1) camelCase `userId` (not the §3·f example's snake_case `user_id`) — one casing across the surface; (2) `GET /auth/session` returns the **login-time profile snapshot** embedded in the token, NOT a fresh live Discord fetch — the §3·f "fetched live" can't hold once the Discord token is discarded (which §3·f also requires), so snapshot is the honest realization; (3) role re-check happens only at a full bounce (≤ the 8h cap), not on refresh (refresh skips Discord); (4) `/auth/session/refresh` takes the refresh token in the `Authorization: Bearer` header (the `{host}` body is accepted but not required) — a per-host-API simplification of the §3·f `{host}`-body shape. **WS:** the `/stream` bearer rides `?access_token=` (a handshake can't set a header). **Tier gating:** viewer = reads + stream, operator = + the command `POST`, admin = diagnostics (`_throw`/`_dbcheck`) + reserved (settings/install/audit-config, M5/M8). **Secure-by-default:** an authorization `FallbackPolicy` requires an authenticated caller on any endpoint without explicit `[Authorize]`/`[AllowAnonymous]`; only `/health` + `/api/v1` opt out (the SPA's pre-login reachability probes). **CSRF (added M4·b):** `/auth/discord/start` sets a one-time HttpOnly `state` cookie (stateless double-submit; `SameSite=Lax`, `Secure` only under https); `/auth/discord/callback` returns `400 invalid_state` on a missing/mismatched state before any Discord exchange. **M4·b — LIVE-VALIDATED 2026-06-15:** the real Discord exchange + bot-token role lookup resolved an admin login end-to-end (§8); login endpoints `503` only until the Discord app/bot-token/guild/role-map are configured. |
| Audit record + closed `action` vocabulary + SQLite schema | **M5** | `architecture.html §3·d` — **frozen 2026-06-15.** **Endpoint** `GET /api/v1/audit?cursor=&limit=50&severity=&serverId=&actor=` → `{ data, nextCursor }`, **newest first**, keyset on the opaque `rowid` cursor (`RowId < cursor` ordered `DESC`; `nextCursor` = the last row's rowid, or null when the page is short). Filters map 1:1 to indexed columns; `limit` clamped (default 50, max 200). **Record** = `{ id (evt_…), ts (Z), origin, actor:{ kind:user\|system\|token, name, provider:discord\|system\|api? }, action, severity:info\|success\|warn\|danger, target:{ kind,id,name }?, serverId?, hostId?, summary, meta? }`. **WS:** the `audit` topic carries **`audit.append`** (one full record; the client **prepends** — events are immutable). Unlike the metric/status patches it is **NOT supersede-by-latest**: the coalesce key is the unique event id, so distinct appends never collapse. **Action vocab wired in M5** (the honestly-sourceable subset): `server.start\|stop\|restart\|update\|install\|uninstall`, `backup.create\|restore` (from kgsm events), `auth.login\|logout` (API-internal). **Extended in M6·0** (producers now landed): `server.crash` (kgsm-watchdog — `instance_crashed`→warn, `instance_failed`→danger, both `system`-stamped, kgsm-lib 1.9.0) and `network.ports.open`/`network.ports.close` (the CLI-path firewall echoes `instance_ports_opened`/`_closed`, kgsm-lib 1.12.0; ports recorded in `meta`). **Divergence (a server-side additive extension, like origin-nullable):** the §3·d `network` set lists only `ports.open`, but the server also records `network.ports.close` — a real, now-sourceable action — so opens and closes form a symmetric trail (a standalone `files firewall disable` closes ports outside any uninstall and would otherwise go unrecorded); the frontend already accepts unknown actions forward-compat (M2). The api-issued `open_ports` command writes `network.ports.open` **directly** at M6·b (kgsm runs nothing → no echo, the `auth.*` case); there is no api close command (§3·g is open-only), so `ports.close` is cleanly CLI-echo-only — no double-write. **Still deferred (no source yet):** `config.change`/`player.*`/`host.*`/`discord.*`/`settings.change`. **Source model (the no-double-write decision):** kgsm **owns** `server.*`/`backup.*`, so the API records the engine's **event echo** — it never writes an audit row when it issues a command; instead the command path **stamps** `actor`(bearer identity)+`origin`(declared surface) which ride the event and are read back off it. `auth.*` has no kgsm event → written directly (no double-write). **Divergences (the negotiated honest-vs-aspirational call, like the M1·b DTO):** (1) **`origin` is nullable** (the §3·d DDL says `NOT NULL`) — a direct-CLI engine action has no product surface, so the engine emits `null` and we persist that, never fabricate a surface; (2) the example's **`meta.jobId` is not populatable** — no correlation id round-trips the stateless engine, so `meta` holds action-specific detail (e.g. `{oldVersion,newVersion}`, `{blueprint}`, `{source,version}`, the login `{tier}`) instead; (3) the command path's `origin` is **caller-declared** (`ui\|assistant\|discord\|api`, default `api` — literally true; `system` reserved for autonomous engine actions and rejected), **never derived from the actor** (the two axes stay independent). **Honest boundary:** events emitted while the API isn't listening are **never audited** (stateless engine, no backfill) — inherent to a downstream-consumer design. **Storage:** the §3·d SQLite schema, created via **`EnsureCreated`, NOT an EF migration** (greenfield/dev authority — wipe the DB on a schema change). Gated at **viewer** (a core read surface). |
| `network` block (server) + host `openPorts` + `open_ports` command | **M6·b** | `architecture.html §3·g` — **frozen 2026-06-16.** **Per-server, `GET /servers/{id}` DETAIL ONLY** (the first place detail ≠ the `/servers` list element): `network:{ firewall, required[], reachable }`. `required[]` = `{ port, proto:"tcp"\|"udp", open: bool\|null }` derived from `Instance.Ports` (the `GetAll` roster, `instances list --detailed --json` — confirmed to carry structured `ports`), ranges `Expand()`'d to one row per port; **always present** when the instance is known (domain truth, firewall-independent). `open` per row: `true` = the firewall owns a rule covering `(port,proto)` (via `IFirewallService.ListOwnedAsync(instance)`), `false` = no such rule (firewall answered Ok), **`null`** = firewall could not answer. `firewall` ∈ `operational\|down\|unknown\|unsupported\|absent` (block-level liveness: Ok / `FirewallException` / `ListOwnedAsync.Status=Unknown` / `Unsupported` / not-provisioned) — **NOT** a `HostCapabilities` entry and **NOT** in the 2s `LeafHealthMonitor` poll (firewall is socket-activated + idle-exits ~30s; probed **on-demand** per detail view). **Divergences (the negotiated honest-vs-aspirational call, like M1·b `cpuPctCore`):** (1) **`reachable` is RESERVED — always `null`** (the §3·g DDL/prose ask for an end-to-end verdict — "a rule can be applied while the port stays blocked upstream, router NAT/ISP" — but the api has **no upstream prober**; the honest verdict is per-row `open` = host-firewall-rule-present, and the frontend derives "all required open" from `required[].open` itself; the strong name is reserved for a real prober, e.g. a future UPnP/watchdog probe — **rename-not-redefine**, not an overclaiming boolean); (2) per-row `open` and `reachable` are **nullable** — honest-unknown when the firewall can't answer, **never fabricated `false`**. **Per-host, `GET /hosts/{id}` DETAIL ONLY:** `network:{ openPorts:[{ port, proto, app\|null, server }] } \| null` — `ListOwnedAsync(null)` across all instances; `server` = instance name (`FirewallOwnedRule.Instance`), `app` = blueprint id joined from the roster (**`null` when unmapped — never guessed**); the whole `network` is **`null`** when the firewall is absent/unreachable/`Unknown`, **`[]`** when Ok-but-empty (the `Unknown`≠empty distinction preserved); camelCase `openPorts` (not §3·g's snake `open_ports` — one casing, like `userId`). **Command** `POST /servers/{id}/commands { verb:"open_ports", origin? }` — **intent-only, NO client port list** (server derives the target from `Instance.Ports`; accepting a client list would let the browser open anything); gate = 404 unknown server, **no state no-op** (declarative/idempotent — always admissible), 409 if a command is already in flight (shared per-server slot); exec = `EnsureOpenAsync(serverId, Instance.Ports)` → re-probe `ListOwnedAsync(serverId)` verify; **audited by DIRECT write** (action `network.ports.open` — the firewall emits nothing on the `IFirewallService` path, so kgsm runs nothing → no echo → no double-write; the CLI path stays event-echo-only, §M5 row). **Verify push:** `job.patch` (`queued→running→succeeded\|failed`) on `jobs`, a fresh `network.patch` on the **dedicated `servers/{id}/network` topic** (so `server.patch` stays the frozen M1·b `Server` — no contradiction), and `audit.append` on `audit`. **`{opened,reachable,actionId}` realization (§3·g):** `reachable` reserved-`null`; `opened` (the delta) recorded in audit `meta`; `actionId` = the audit `evt_` id on `audit.append`; **`meta.jobId` IS populatable here** (direct-write owns both job + append — the M5 "no jobId" limit was the event-echo path) → the frontend correlates job→audit via it; **no new field on the generic `Job`**. On `EnsureOpenAsync` `Ok=false` (Unsupported/Failed) or `FirewallException` → job `failed` with the backend detail, **never a fabricated success**. **AMENDED 2026-06-16 (firewall ENFORCEMENT axis — Firewall.Contracts 1.1.0 / kgsm-lib 1.14.0):** `firewall` gains **`inactive`** (the authority is reachable but NOT enforcing — e.g. ufw disabled — so it filters nothing). When `firewall:"inactive"`, **every `required[].open` is `true`** (the port is reachable because the firewall is OFF, not because a rule allows it) and the host `network.firewall` is `"inactive"` with a (typically empty) `openPorts` that means **all open, not nothing open**. **⚠ Security-of-presentation (the one new risk of the `open:true`-when-inactive choice): a client MUST read `firewall` alongside `open` — `open:true` under `operational` = "allowed by a rule"; `open:true` under `inactive` = "the host has NO firewall"; a UI that greens a check on `open:true` without consulting `firewall` paints 'all clear' over 'no protection'.** An `Ok` list reply with `unknown` enforcement (a pre-1.1.0 authority) falls back to the prior `operational`/rule-present behaviour. The `open_ports` command's `EnsureOpenAsync` may now return **`applied-inactive`** (the rule is staged on an inactive firewall — a success: persists + enforces on the operator's next `ufw enable`); it is still audited `network.ports.open` (a real config change) but the row says **"staged firewall ports …"** with `meta.enforced:"false"`, never "opened" (recording an enforced open that didn't happen would be the very lie this fixes). **LIVE-VALIDATED 2026-06-16 (both transitions, wire + api):** the 1.1.0 daemon emits `enforcement:inactive`/`enforcing` + the `applied-inactive` outcome, the bundled CLI returns exit 0 on an inactive open (no install-abort), and `GET /servers/factorio-test` reads `firewall:"inactive"` + all-`open:true` with ufw OFF → `firewall:"operational"` + rule-gated `open:true` with ufw ON (host grid likewise, `app:factorio` join intact). |
| Alert record + raise/resolve/retract | **M6·a** | `architecture.html §3·c` — **PROPOSED 2026-06-16 (built + self-validated; sign-off pending — NOT yet frozen).** **Endpoint** `GET /api/v1/alerts?status=firing\|resolved&since=24h` → **`{ data }`** (unpaginated — the feed trends empty, unlike `/audit`'s `{data,nextCursor}`; **divergence**). `status=firing` (default) = one record per live condition; `status=resolved` = the rear-view that cleared within `since` (default + **max 24h** — the rear-view ages off). **Record** = `{ id, severity:danger\|warn\|info, source, title, detail, serverId?, hostId, anchor?, status:firing\|resolved, raisedAt(Z), escalated, attempts, resolvedAt?(Z), resolution? }`. `id` is **condition-derived + stable** (`crash:<serverId>`) so a re-fire upserts and escalation re-pushes the SAME record (never a per-raise id). **`resolution`** = `{ by:"system" (always — the server observed the clear), source, reason, actionId? }`. **WS** (topic `alerts`): `alert.raise` (the full record, status `firing`; re-pushed to flip `escalated`), `alert.resolve` (`{ id, resolution }` — the client stamps `resolvedAt`), `alert.retract` (`{ id }` — gone, no rear-view). **Coalesce key = the alert id** (`AlertEntityKey`) so a resolve/retract supersedes a still-queued raise (the `ServerPatch`/`ServerRemoved` precedent, NOT audit's per-append unique key); a torn-down slow client re-hydrates via `GET /alerts` (§3·j). **Read-only** — no complete/dismiss/PATCH. **Source model (M6·a = crash only):** the watchdog's supervision state via kgsm-lib `IWatchdogClient.ListAsync()` (poll-as-authority — the poll interval IS the raise debounce; api-owned 30s resolve probation; mirrored escalation; retract on a vanished instance; honest-unknown on a blind poll; rebuilds on restart; **native instances only**). **The alert↔audit bridge** `resolution.actionId` is the `evt_` id of a `server.start`/`server.restart` audit row, set only for an **operator/api** recovery (the consumer hands it off after the write); **an autonomous watchdog restart emits no audited action** (verified against the watchdog source), so a pure auto-heal links to **null** — never fabricated (the doc's `evt_restart_mc` presumes an audited restart we don't have yet; deferred). **Divergences needing sign-off (the negotiated honest-vs-aspirational call, like M1·b / M6·b's three decisions):** (1) top-level **`hostId`** (beyond the §3·c example's `anchor.hostId`) for the SPA host filter (§4·d); (2) the unpaginated **`{data}`** envelope; (3) `source` reserves `host-monitor`/`metrics`/`assistant` but **only `watchdog` is emitted** (the rest have no honest source — never fabricated); (4) **`anchor.surface:"server"`** is a best-effort hint; (5) `alert.resolve` is `{id,resolution}` (client stamps `resolvedAt`). Gated at **viewer**. **Built + self-validated** (unit + smoke) **+ LIVE-VALIDATED 2026-06-16** (real watchdog crash on `factorio-test` → `warn` raise → 30s-probation resolve → `actionId:null` auto-heal, no flap; §8). Contract still PROPOSED — frontend sign-off pending. |
| Assistant SSE event vocabulary (proxy vs re-wrap) | M7 | `architecture.html §5·a`; keystone O1 — **RESOLVED 2026-06-19: near-verbatim relay, §5·a shaping pushed upstream into the assistant** (`kgsm-llm/docs/m7-sse-5a-spec.md`, Phase 1 DONE `bda373a`); **fork LOCKED (a)** — confirmed commands execute via M3, `command.verified` is NOT a turn event. **Backend built & self-validated 2026-06-19** (`POST /api/v1/assistant/turn`, viewer-gated, capability-gated, trusted co-located relay auth: shared `X-Relay-Secret` + forwarded Discord identity; smoke 42/42 incl. a stub-assistant relay round-trip proving secret+identity forwarding + byte-faithful streaming, + tests 109). Only a real-model (Ollama) end-to-end remains. |
| `LibraryEntry` DTO (the catalog) | **M8·a** | `architecture.html §3·h/§3·i` — **frozen 2026-06-19.** `GET /library?q=&category=` (viewer) → `LibraryEntry[]`: `{ id, name, type:"native"\|"container", steamAppId?, clientSteamAppId?, isSteamAccountRequired, ports[{start,end,proto}], specs{maxPlayers?,minRamMb?,recommendedRamMb?,baseDiskMb?}, cover, rawgSlug }`. Pure kgsm-lib blueprint scrape (`IBlueprintService.ListDetailed`); engine-base degrade → `[]`. **Divergences (the negotiated honest-vs-aspirational call, like M1·b):** (1) **`cover` RESERVED — always `null`** (RAWG resolution is a later increment; honesty bars a fuzzy name→RAWG match — resolve only from an exact key like `SteamAppId`→Steam CDN, never mis-attribute art); (2) **`rawgSlug` RESERVED — always `null`** (no curated slug on a blueprint; `§3·i`'s backend lookup hint); (3) `name` falls back to the blueprint `id` when metadata is uncurated (all blueprints today → `name==id`, never a guessed display name); (4) `steamAppId`/`clientSteamAppId` are **`null`** for a non-Steam blueprint (NOT the `Server` DTO's `"0"` sentinel — honest-null on this new surface); (5) `specs` keys always present, every value `null` today (uncurated upstream — `null`≠0); (6) `ports` is the blueprint's **declared default** spec, structured `[{start,end,proto}]` — emitted **directly by kgsm** on `blueprints … --json` (the 1.10.0 canonical-port-format migration extended to the blueprint surface; kgsm-lib 1.17.0 types `Blueprint.Ports` as `List<PortMapping>`), so the api never parses a port string; (7) `category` query is **reserved/inert** (no honest genre source). Gated at **viewer**. **Built + self-validated** 2026-06-19 (live 29-blueprint read proving the bash→lib→api chain + the `q` filter; smoke 44/44, tests 120). |
| Install body (honored vs reserved) + uninstall | **M8·b** | `architecture.html §3·h` — **frozen 2026-06-19.** `POST /servers` body `InstallRequest { blueprint(required), name?, origin?, + reserved: hostId?,version?,port?,queryPort?,slots?,dir?,password?,autostart? }` → **`202` + `{ job }`** (NOT a server — install is async; the new server appears on `/servers` with a backend-assigned id when the job settles). **Honored:** `blueprint`, `name`, `origin`; **everything else accepted-but-inert** (additive-only — sending it keeps the schema forward-compatible). `DELETE /servers/{id}` (uninstall) → `202` + `{ job }`; `origin` rides `?origin=`. Both **operator-gated**; the `job` is the frozen M3 shape with `verb:"install"`/`"uninstall"`. **Divergences (the negotiated honest-vs-aspirational call, like M1·b):** (1) **`name` is the kgsm instance name, not a free-text display label** — kgsm validates it as an id and falls back to an auto-generated `blueprint-suffix` if it isn't a usable unique name (a true display name is deferred upstream — blueprint metadata curation, never silently dropped); (2) the reserved fields are **inert, never half-applied** (`dir`/`version` would mis-map — `version` is a build channel, not a kgsm game version — so they wait for an honest mapping); (3) **`autostart` is inert** (the post-install start chain is owed). **Gate:** install `400` (missing/unusable blueprint-or-name — generate-id's real detail) · `409` (install in flight for the resolved name) · `503` (engine unprovisioned); uninstall `404` (unknown id) · `409` · `503`. **Audit:** echo-path, **no double-write** — the command stamps actor+origin, kgsm emits `instance_installed`/`instance_uninstalled`, the M5 consumer writes `server.install`/`server.uninstall` (NOT a direct write — the lifecycle case). **Verify:** install → `server.patch` (new server); uninstall → `server.removed` tombstone. Backend built & self-validated (smoke 47/47 gate-only + tests 135, incl. the no-double-write proof + the malformed/type-mismatched-body `{error}`-envelope guarantee); **LIVE-VALIDATED GREEN end-to-end 2026-06-19** (real install + uninstall round-trip — surfaced + fixed an upstream `kgsm uninstall` interactive-only gap, kgsm-lib 1.18.0); committed to `main` (not pushed). |
| `MeResponse` (the identity surface) | **M8·c** | `architecture.html §3·f` surface table (the "Profile" resource) — **frozen 2026-06-19.** `GET /me` → `MeResponse { user: SessionUser{ id, username, display, avatarUrl? }, tier, scopes[] }`. A pure projection of the session bearer's claims (no engine/leaf/DB touch): the Discord identity snapshot captured at login + the resolved authorization `tier` + the granted `scopes`. Gated at **`[Authorize]`** — any authenticated caller, NOT viewer — so a `none`-tier caller (verified identity, no role on this host) can read "who am I / why am I 403 elsewhere"; no bearer → the `401` envelope. **Divergences (the negotiated honest-vs-aspirational call, like M1·b):** (1) **read-only** — the surface table lists `/me` as GET+**PATCH** ("display name, handle, **density**"), but the editable half needs a per-panel preference store **deliberately not built** (architecture.html's statelessness note: per-user/panel prefs that follow a user across devices are out of scope), so PATCH + density are deferred, never faked; (2) the profile is the **login-time snapshot**, not a fresh live Discord fetch (the §3·f no-retained-token divergence, shared with `/auth/session`); (3) the honest **delta over `/auth/session`** (which returns `{user,scopes}`) is the **`tier`** — the one fact the SPA gates its controls on; `display`/`username` fall back to the handle, never a guessed label. **Built + self-validated** 2026-06-19 (smoke 48/48: the wire shape under the auth-disabled synthetic admin + `/me` in the no-bearer 401 sweep; tests 143 (+8 `MeTests`): tier-reflected-verbatim, the none-tier `200` reachability, refresh-as-access/wrong-signature `401`). |
| Discord integration (`/integrations/{provider}`) | **M8·c** | `architecture.html §3·e` — **Increment A frozen 2026-06-19.** Provider-agnostic outbound notification routing (Discord first; Slack/Telegram via `/integrations/{provider}`). **Admin-gated.** `GET /integrations` → `[{ provider, configured, enabled }]`; `GET /integrations/{provider}` → the §3·e record `{ provider, webhook:{configured, hint}, channelLabel, bot, enabled, events[] }` (events = the server-defined catalog ⋈ the user's `{enabled,cadence,ping}`); sparse `PATCH /integrations/{provider}` (the `webhook` field sets/rotates the secret; a blank string clears it); `POST /integrations/{provider}/test` → `202 {ok,posted,channelLabel}` on a real send. **Divergences (the negotiated honest-vs-aspirational call, like M1·b):** (1) **`bot` is always `null`** — one-way **webhook** delivery only; the §3·e two-way control bot + slash-commands are **out of scope** (kgsm-bot's interactive surface), so honestly null, not a fabricated connection; (2) the catalog lists only **deliverable** events (`online·offline·crash·update·installed·backup`) — `resource` (no threshold-alert source) and `join` (no player tracking) are **omitted**, never faked; (3) the webhook secret is **masked on read** (a `hint`, never the URL), **write-only on PATCH**, and **never logged** (the webhook URL *is* the secret, so the typed HttpClient is registered `.RemoveAllLoggers()` — else the factory's default handler logs `POST {uri}` at Information; pinned by a log-capture test) — stored plaintext in the host-local SQLite (consistent with the env-stored bot token on this single trusted host); (4) **`cadence` enforcement is incremental** — **`every` now ENFORCED** by the Increment B delivery worker (the always-on audit-tap → providers); `once`/`digest` are still **deferred to Increment C and deliver ZERO** (not fewer) — the skip is logged ("set 'every' to receive it"), never a silent black hole (the M8·b reserved-field pattern, made honest); (5) **coexists with `kgsm-bot`** (which posts per-instance channels via the bot gateway) — the API posts its own configured webhook, no double-post unless aimed at one channel. `/test` is honest: `409` unconfigured · `502` delivery-failed · `202` ok — never a faked ok. Gated at **admin**. **Increment A built + self-validated** 2026-06-19 (smoke 52/52 incl. the masked-secret PATCH round-trip + the honest catalog; tests 166 (+23): the provider mask/validate/test-send with faked HTTP, the API admin gate, and the advisor-caught no-token-in-logs guard). **Increment B (delivery worker) built + self-validated** 2026-06-19 (tests 190 (+24)): `AuditService.AppendAsync` → bounded `INotificationBus` (`DropOldest`, logged) → `NotificationDeliveryWorker` (scope-per-event, gates enabled+secret+rule.enabled+`every`), `INotificationProvider.SendAsync` (Discord per-action formatting + optional `Settings["pingRoleId"]` ping with `allowed_mentions` scoped to that role). **Two B-frozen behaviors:** **`server.restart` also maps to `online`** (a completed restart = back up — closes the crash↔back-online pairing incl. the watchdog auto-heal), and a **per-`(provider,server,catalog)` 60s anti-spam window** coalesces a crash-loop (mass-reboot is N distinct keys → not suppressed, bounded by host server count). Live-validate with a real webhook **owed**. |
| File browser (`GET/PUT /servers/{id}/files…`) | **Tier 3 #12** | `docs/file-browser-plan.md` — **frozen 2026-06-24.** The instance-working-dir file browser + editor behind kgsm-web's `FileBrowser`. **`GET /servers/{id}/files?path=`** → `{ path, truncated, entries[{ name, kind:file\|dir\|symlink\|special, sizeBytes?, mtime?, editable?, lang?, reason? }] }` (lazy, one dir/request; dirs-first/alpha; cap `KGSM_API_FILES_MAX_ENTRIES`=200 → `truncated:true`, never a silent gap). **`GET /servers/{id}/files/content?path=`** → `{ path, encoding:"utf-8", content (RAW), sizeBytes, mtime, etag:"sha256:…" }`; binary (NUL/invalid-UTF8) → `409 file_binary`, > `KGSM_API_FILES_MAX_EDIT_BYTES`(2 MiB) → `409 file_too_large`. **`PUT …/content?path=`** body `{ content, etag?, origin? }` → `200 { path, sizeBytes, mtime, etag }`; **save-existing only** (no create → `404`), atomic (temp+fsync+rename, mode preserved), `412` on etag drift, `409` on binary/too-large/non-regular target. **BOTH read + write are operator-gated** (contents hold secrets), not viewer. **Security (the load-bearing part):** the jail root is `Instance.WorkingDir` (re-derived per request via the chokepoint, never cached); every candidate is canonicalized following symlinks at **every** component (POSIX realpath — a naive `..`-strip + prefix check misses an intermediate-dir symlink, verified empirically) and required to stay inside the real jail (escape → `404`, never leaks the host path); only regular files open (`lstat` via P/Invoke — FIFO/socket/device refused, the live `.<name>.sock` hazard). **Audit:** `file.write` is a **DIRECT** write (the `auth.*` case — kgsm runs nothing, no echo, no double-write); `meta` carries `{path,sizeBytes,sha256}` and **NEVER the content** (secret hygiene — configs hold rcon passwords/tokens). **No upstream change** (no kgsm/kgsm-lib/monitor/watchdog), no EF migration (audit reuses `AuditEntry`). **Built + self-validated + LIVE-VALIDATED on both real servers 2026-06-24** (smoke 65/65 incl. list/read/jail/save/412/404/audit-no-content; tests 345 (+35: the `InstanceFileService` jail/symlink/special/binary/atomic-save unit suite + the `FileBrowserApiTests` gate/contract/secret-hygiene suite); 0-warn Release). Frontend WIRED (kgsm-web `FileBrowser` + the `api.put` seam). |
| Metrics history (`/{servers,hosts}/{id}/metrics/history`) | **M9** | `docs/metrics-history-plan.md` — **frozen 2026-06-24.** **Endpoints** `GET /servers/{id}/metrics/history?range=1h\|24h\|7d\|30d` and `GET /hosts/{id}/metrics/history?range=…` (viewer-gated, additive within `/api/v1`). **Tier selection (auto by range):** range ≤ raw retention (24h) → raw (`sample` table, ~15s step); range > 24h → rollup (`rollup` table, 5min step). **Response** `{ entityId, kind:"server"\|"host", range, step(seconds), tier:"raw"\|"rollup", series:{ metricName: [{ ts(Z), value, min?, max?, n? }] } }`. Rollup-tier points carry `min`/`max`/`n` (the band + coverage); raw-tier points are `{ ts, value }` only. `tier` disambiguates. **Gaps are absent points** — sparse series, no carry-forward; the client renders breaks. Unknown id → `404 {error}`; history disabled / monitor never seen → empty `series` (200). **Endpoint path: `/metrics/history`** (deliberately NOT the plan's original `/metrics?range=` — avoids collision with the existing live `Server.metrics` block). **Storage:** dedicated `metrics.db` (D4), EnsureCreated, WAL + auto_vacuum=INCREMENTAL. **Server metrics:** `cpuPctCore`, `memBytes`, `ioReadBps?`, `ioWriteBps?`, `pids`, `diskBytes?`. **Host metrics:** `cpuTotalPct`, `memUsedKb`, `memTotalKb`, `memAvailableKb`, `swapUsedKb`, `loadOne`, `loadFive`, `loadFifteen`, `diskReadBps`, `diskWriteBps`. Null metrics → absent row (honest gap). **Config:** `KGSM_API_METRICS_HISTORY_DISABLED` (master switch), `KGSM_API_METRICS_HISTORY_DB`, `KGSM_API_METRICS_PERSIST_MS` (15s default, 5s floor), `KGSM_API_METRICS_RAW_RETENTION_HOURS` (24), `KGSM_API_METRICS_ROLLUP_STEP_MIN` (5), `KGSM_API_METRICS_ROLLUP_RETENTION_DAYS` (30), `KGSM_API_METRICS_MAINT_MS` (60s). **Built + self-validated 2026-06-24** (tests 365/smoke 71 incl. durability across restart). |
| Host identity card + `PATCH /hosts/{id}` | **M8·d** | `architecture.html §4·a` (host scope) — **frozen 2026-06-29.** The descriptive "who/where/what is this host" block, alongside the live `capabilities` ("what can it do"). **Read:** `GET /hosts` + `GET /hosts/{id}` gain `identity:{ region, os:{ name?, kernel?, arch }, runtime, build, startedAt(Z) }` (present on **both** list and detail — cheap + static, no leaf, like `panelVersion`); the editable display label stays `Host.label` (not duplicated). The **open** `GET /api/v1` handshake doubles as the host's **public identity card** (read pre-login): it gains `build`, `label`, and `region` (region **omitted when unset** via `JsonIgnore WhenWritingNull`). **Write:** `PATCH /hosts/{id}` body `HostPatch { label?, region? }` → the refreshed host detail; **admin-gated** (host config; tightens the class-level viewer gate). Sparse: an absent field is unchanged, an **explicit empty string clears** the override (back to the `KGSM_API_*` config default); values are length-bounded (100) → `400 bad_request`, never a silent truncation; unknown id → `404`. **Two honesty classes, never conflated (the negotiated honest-vs-aspirational call, like M1·b):** (1) **`region` (+ the label override) is operator-declared** — config (`KGSM_API_REGION`/`KGSM_API_HOST_LABEL`) seeds the default, a `PATCH` override wins at runtime; an **arbitrary free string** (NOT a restricted enum), **`null` when neither is set** (honest unknown — never IP-geolocated or guessed); (2) **`os`/`runtime`/`build`/`startedAt` are runtime-derived** — `os.name` from `/etc/os-release PRETTY_NAME`, `os.kernel` from `/proc/sys/kernel/osrelease` (each `null` when unreadable), `os.arch` from `RuntimeInformation`, `runtime` = `FrameworkDescription`, `build` = the assembly **InformationalVersion** (`<Version>`+git SHA, e.g. `0.1.0+2e8e593692c3`) — the honest "which build is this host running" value that **replaces the old fabricated-semver concern** (`ApiInfo` used to hardcode `"v1"` because there was no `<Version>`); `startedAt` = the API process start. **Build vs route version:** `build` is the build axis; `version`/`panelVersion` stay the **route** version `"v1"` (`ApiInfo.ApiVersion`) — a separate, unchanged axis. **Sensitivity split:** the low-sensitivity, operator-declared `label`/`region` + `build` ride the open handshake (so the multi-host connect screen labels "eu-west / Hotrod" pre-login); the OS/kernel detail stays **auth-gated** on `GET /hosts/{id}` (mild fingerprinting). **Persistence:** a single-row `host_settings` table (region/label overrides), the API's own operational metadata like `integrations`/`rawg_entry` (the domain stays live-scraped). EnsureCreated **+ an idempotent `CREATE TABLE IF NOT EXISTS`** in `HostSettingsStore` so the table appears on an **already-deployed DB without wiping the shared append-only audit log** (the one EnsureCreated-no-ops-on-existing-DB escape that preserves audit). **Built + self-validated 2026-06-29** (tests 409 (+12 `HostIdentityTests`: identity shape on list+detail, the handshake card, the admin set→reflected-everywhere→clear round-trip, sparse merge, the 401/403 split, unknown-id 404, over-length 400, config-default-then-override); smoke 88/88 (+4: handshake card, host card, PATCH round-trip incl. handshake reflection, over-length 400; + the no-bearer 401 sweep now covers `PATCH /hosts/{id}`); also fixed a latent smoke bug — the M9 per-server-history check hardcoded `factorio` instead of `$FIRST_ID`). |
| Slack integration (`/integrations/slack`) | **M8·c** | `architecture.html §3·e` `/integrations/{provider}` — **Increment C frozen 2026-06-19.** The **second provider**, validating the webhook-family abstraction. `GET/PATCH /integrations/slack` → `SlackIntegrationView { provider, webhook:{configured,hint}, channelLabel, enabled, events[] }` — the **same** masked-webhook + catalog⋈rules shape as Discord, but with **no `bot` block** (Slack incoming webhooks have no Discord-style two-way control bot — omitted, not a fabricated null; the frontend renders per `provider`). Secret = a `hooks.slack.com/services/...` URL (validated, masked `…/services/{T}/{B}/{tok}***`, write-only, never logged — same `.RemoveAllLoggers()` care). Message is Slack mrkdwn (`*bold*`) with `&<>` escaped; an optional ops ping via `Settings["pingSubteamId"]` → `<!subteam^id>`. Same admin gate, same `every`-cadence delivery (the worker is provider-agnostic). **Honest scope (advisor):** the shared `WebhookNotificationProvider` base validates the **webhook-secret-URL family** (Discord + Slack); **Telegram** (bot token + fixed endpoint + `chat_id`) would implement `INotificationProvider` directly — it is the next real test of the interface, not this base. **Built + self-validated** 2026-06-19 (tests 207 (+17); smoke 54/54 (+2): the list asserts both providers, the no-`bot` view, the masked round-trip). Live-validate with a real Slack webhook **owed**. |

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
    Controllers/              # [M0] Health, Meta, Diagnostics · [M1·a] Hosts → [M1·b] Servers · [M2] Stream (WebSocket) → [M3] Commands → [M4·a] Auth → [M5] Audit → …
    Contracts/                # [M0] ErrorEnvelope, HealthStatus, ApiInfo · [M1·a] HostDto · [M1·b] ServerDto (Server/ServerMetricsDto/ServerStatus) · [M2] StreamDto (HostMetricsDto/ServerRemoved) · [M3] CommandDto · [M4·a] AuthDto (CallbackResult/RefreshResponse/SessionResponse)
    Realtime/                 # [M2] BUILT — StreamProtocol (central topic/type vocabulary), StreamMessage (envelope), StreamHub (registry+fan-out), StreamConnection (coalescing duplex loops), MetricsPump + DomainPump (gated push pumps)
    Infrastructure/           # [M0] ApiExceptionHandler (IExceptionHandler→500 envelope), ApiErrors (envelope writer)
    Json/                     # [M0] ApiJson (shared options config) + Iso8601UtcConverter
    Data/                     # [M0] AppDbContext (+ Probe de-risk) → [M5] AuditEntry; Migrations/ from M5. (M4·a auth is STATELESS JWT — no Session table.)
    ApiOptions.cs             # [M1·a] config consolidation via IConfiguration (host id/label, monitor/watchdog sockets, assistant url; [M4·a] + auth: signing key, Discord app/bot/guild, role→tier map, AUTH_DISABLED) — built
    Services/
      Leaves/                 # [M1·a] MonitorClient (cached scrape + /health) + AssistantClient (typed HttpClient; /health probe, grows tools/SSE at M7) · [M2] LeafHealthMonitor (always-on /health poller → §4·b capability truth + capabilities.patch flips; watchdog via kgsm-lib IsReadyAsync)
      Aggregation/            # [M1·a] HostAggregator (capacity from /metrics + §4·b capabilities from LeafHealthMonitor) · [M1·b] ServerAggregator (lib status ⋈ monitor metrics) · MetricsMapping (shared snapshot→DTO, REST==WS)
      Commands/               # [M3 built] CommandGate (admissibility) + JobRegistry (in-memory, 1-in-flight/server) + CommandRunner (scope-per-job exec + job.patch + verify server.patch)
      Auth/                   # [M4·a+b BUILT, live-validated] IDiscordIdentityResolver (the seam) + DiscordIdentityResolver (real, live-validated) · SessionTokenService (HMAC JWT mint/validate) · AuthTier/TierAuthorization (hierarchical policies) · DisabledAuthHandler (escape hatch) · AuthController state-cookie CSRF. Stateless JWT, no session store.
      Audit/  Alerts/ Ports/  # [M5]/[M6] event consumer, alert engine, port intent
      Assistant/              # [M7] SSE relay
      Install/ Library/ Settings/ Integrations/  # [M8]
  tests/Api.Tests/            # [M4·a BUILT] xUnit + WebApplicationFactory; the auth tier matrix + callback/refresh/session flow with the Discord seam faked (FakeDiscordResolver). Grows per milestone.
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
`/health` + `/api/v1` (camelCase, **ISO-8601 UTC `Z`** timestamps via the shared
`Iso8601UtcDateTimeOffsetConverter` on MVC + HTTP JSON options), an **EF Core + SQLite
round-trip** (`_dbcheck`), the error envelope on a **real 500** (`_throw`, via
`ApiExceptionHandler`) and a **404** (`{error:{code:not_found}}`, via `UseStatusCodePages`),
and CORS request + preflight headers.

**Contracts frozen from `architecture.html` (verified verbatim, not invented):** error
envelope `{error:{code,message,details?}}` (§6 line 1010); base path `/api/v1`,
path-versioned, additive-only (§6); timestamps ISO-8601 UTC `Z` (§6). `/health` is
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

### M1·b — 2026-06-14 · servers (the join: kgsm-lib ⋈ monitor) self-validated; frontend gate PENDING
**Status:** the project's central join is built and verified (the honest `Server` DTO; domain +
run-state from kgsm-lib joined with per-instance metrics from the monitor, keyed on instance id);
the collaborative gate (frontend swaps `serversStore` mock → real) is deferred with M0/M1·a's.
Not marked "frontend validated."

**The honest `Server` DTO (frozen in §6).** This is the project's most important contract
conversation, and the divergence from the aspirational `architecture.html §3` example *is* the
contract: `status` is `running|stopped|unknown` (from `Reading<InstanceRuntimeStatus>` — measured
bool → running/stopped, any non-measured/missing reading → `unknown`), never the aspirational
`online|offline|updating|crashed|installing` (those transitional states need the M3 job tracker +
crash detection that don't exist). Metrics keep the monitor's native units — `cpuPctCore` (% of one
core, **can exceed 100**), `memBytes`, nullable `io*` — nested under a `metrics` block that is
**`null`** when no per-server sample exists (monitor absent/unreachable, or the server simply isn't
running). Omitted as unsourceable (fabrication is what scrapped the old api): `players`, `cpu` 0–100,
`ram.max`, `ip`, `updatedAt`, and the curated `game` display name — we emit the real `blueprint` id
(the clean `<name>` from the unified `<name>.bp.yaml`), metadata curation deferred and never guessed.
Keys are always present with explicit nulls so the SPA binds a stable shape.

**Wiring.** kgsm-lib is **engine/base, not a leaf** (keystone §4): the api co-locates with a kgsm,
so it is provisioned-by-default at the AUR-packaged path (`KGSM_API_KGSM_PATH=/usr/bin/kgsm`); an
unconfigured engine is a *misconfiguration* surfaced as an empty `/servers` + a one-time log, NOT a
§4·b "engine" capability (there is none). `IInstanceService` is process-based (it shells `kgsm.sh`),
so the kgsm event socket (`KGSM_API_KGSM_SOCKET`) is only a registration formality here — the event
consumer that opens it lands at M5; the kgsm-lib singletons are lazy, so a non-existent socket never
blocks startup. `IInstanceService` is transient, resolved per-request from the provider (the same
optional-resolve pattern `HostAggregator` uses for the watchdog client). The two blocking kgsm-lib
spawns (`GetAll`, `GetAllStatuses(fast:true)`) run on the thread pool concurrently with the async
monitor scrape. `fast:true` skips the slow per-instance update-check (Version.Latest goes null — not
emitted anyway); `Version.Current` is still reported.

**Self-validated:** `scripts/smoke.sh` → **18/18** (8 M0 + 4 M1·a + 6 M1·b), two phases.
*Phase A* (no monitor, live dev kgsm at `/home/heisen/tks/kgsm/kgsm.sh`): the honest DTO shape
(stable keys, valid `status`/`runtime` enums, this host's `hostId`), every server `metrics:null`
(degrade honesty — never a fabricated zero), and the `{unknown}` 404 `{error}` envelope. Live read
off this host: one instance `factorio-test` (`native`, blueprint `factorio`, version `2.0.76`,
`stopped`) — so Phase A live-proves the **domain read, the status mapping, and the null branch**.
*Phase B* (an embedded stub monitor — a unix socket serving a canned `Snapshot` with one
per-server row keyed to the real instance): the **join's present-branch** — a `cpuPctCore` >100 and
a null `ioWriteBps` carried through *verbatim*, keyed by id, on both the list and `{id}` paths — plus
the host happy path (metrics `operational` + capacity present) now deterministic with no external
monitor. **Honesty boundary recorded:** the non-null join is **stub-proven** (this box has no running
instance to scrape a real cgroup/proc row from); the domain read and the null branch are **live-proven**.

**Found at the gate:** `Instance.Blueprint` (`Path.GetFileNameWithoutExtension`) yields `"factorio.bp"`
for a unified `factorio.bp.yaml`, and the status path's `Configuration.Blueprint` is `"factorio.bp.yaml"`
— neither is the clean game id. The DTO strips the compound `.bp.yaml`/`.bp.yml` suffix deliberately to
get `"factorio"`. Frozen as the wire value for `blueprint`.

**Coverage honesty (what is NOT exercised):** (1) the join key's **monitor side** (`ServerMetrics.Id`
== instance name) is contract-asserted + stub-asserted, never observed from a live monitor row — the
non-null join awaits a *running* instance + the redeployed monitor (its 1.3.0 build isn't redeployed,
keystone). (2) The `status:"unknown"` arm is **code-path-only**: this host's one instance is a measured
`stopped`, so nothing produces a non-measured `Reading` to map to `unknown` in the live run (the smoke
asserts `status ∈ {running,stopped,unknown}` but only hits `stopped`). (3) The kgsm-lib spawns are
bounded by the lib's own `ProcessRunner` (the `Default` 30s tier kills the process tree on overrun →
failure → our caught empty list), so a hung `kgsm.sh` degrades rather than stalling `/servers` forever
— no api-side process timeout is added (the engine is base; trusted host until M4).

**Owed to the frontend at the gate:** **not a clean backing-flip** — the server cards must render
`cpuPctCore` (not `cpu` 0–100) and `memBytes` (not `ram{used,max}`), drop `players`/`ip`/`updatedAt`,
read `blueprint` instead of a curated `game` name, and handle `status:"unknown"` and `metrics:null`
as first-class. Agree these renderings before the store swap.

### M2 — 2026-06-15 · realtime WebSocket + the capability/health model self-validated; frontend gate PENDING
**Status:** the per-host realtime stream is built and verified end-to-end against a deterministic stub
monitor, including the full degrade→recover capability lifecycle. The collaborative gate (frontend
`realtimeStore` connects, applies patches, falls back to polling on drop, re-hydrates on reconnect) is
deferred with the M0/M1 gates. Not marked "frontend validated."

**The stream (frozen in §6).** Raw ASP.NET Core WebSockets at `GET /api/v1/stream` (not SignalR — the
hand-rolled `{topic,type,data}` envelope IS the contract). Inbound `{type:subscribe|unsubscribe,topics[]}`;
unknown command types and unknown/future topics accepted silently (forward-compat for `jobs`/`audit`/…).
Patch-only, no snapshot-on-subscribe (the client hydrates via REST per §3·j). All topic/type strings are
centralized in `Realtime/StreamProtocol.cs` — none inline. Three gated pumps fan out through a per-host
`StreamHub`: `MetricsPump` (~1s monitor scrape → `servers/{id}/metrics` + `hosts/{id}/metrics`),
`DomainPump` (~3s join diff → `servers`, status/roster only — change-detection ignores the metrics block
so it never double-streams the 1s firehose), and the always-on `LeafHealthMonitor` (→ `hosts/{id}/capabilities`).
Backpressure is **coalesce-to-latest per key**: a slow client gets the newest frame, never an unbounded
backlog; a stalled send is torn down → §3·j reconnect. One shared `MetricsMapping` makes a WS tick
byte-identical to the REST element it patches.

**The capability/health model (the conversation that shaped this milestone).** Two axes, never conflated:
`provisioned` is the **fixed** capability *set* (resolved once from config, negotiated at connect, never
flips at runtime); `status` is the **live availability**, driven by an always-on `LeafHealthMonitor` that
polls each provisioned leaf's health every ~2s. A leaf failing flips only `status`
(operational→down→operational) with `provisioned:true` — "temporarily unavailable, still there", never
"lost"; `provisioned:true,status:down` IS the down notification, never softened nor suppressed. `since` is
now stamped (the time **this api observed** the flip, not an authoritative leaf-change time). The monitor
is the single source feeding both REST `GET /hosts` and the `hosts/{id}/capabilities` stream, so they
cannot disagree. **Correctness fix:** M1·a inferred metrics status from `/metrics` frame-presence — a bend
of the "metric-presence ≠ status" invariant (CLAUDE.md #2). Status now comes from `/health`, decoupled:
a warming monitor is `operational` with `null` capacity. Check 12 was reweakened to `capacity present ⇒
operational` accordingly (it had encoded the now-false `operational ⇔ present`).

**Leaf health endpoints — verified, then unified upstream (2026-06-15).** Originally split: the monitor
served `GET /healthz` (`ok\n`), the assistant `GET /health` (`{status:ok}`), the watchdog `GET /ready` over
its control socket (reached only via kgsm-lib `IsReadyAsync` — the chokepoint invariant; the api never opens
that socket). **Decision (user, 2026-06-15):** standardize the whole ecosystem on a uniform **`GET /health`**
(`200` ⇒ the leaf can provide its capability; anything else / no answer ⇒ unavailable, retry until `200`).
**Done the same day — no longer wired-ahead-of-upstream:**
- **kgsm-monitor** `/healthz` → `/health` (rename; zero consumers broke — confirmed by a workspace sweep).
- **kgsm-watchdog** merged its split (`/healthz` liveness + `/ready` readiness) into a single `/health`
  carrying the *readiness* semantics (200 ready / 503 + `{ready,detail}` reason); `/ready` kept as a
  **deprecated alias** for one transition release so a not-yet-rebuilt kgsm CLI / kgsm-lib never silently
  falls off the daemon onto the direct-spawn path.
- **kgsm-lib 1.7.0** `WatchdogClient` now probes `/health` (was `/ready`); the api still pins **1.6.0**, so
  its watchdog probe rides the `/ready` alias until it adopts 1.7.0 — then the alias can be dropped.
- **This api:** `MonitorClient`/`AssistantClient.CheckHealthAsync` already GET `/health` (unchanged); the
  api's **own** ops endpoint was renamed `/healthz` → `/health` (HealthController) for one uniform path.
So against a current monitor the metrics capability now reads correctly from `/health`; the smoke still uses
a stub answering 200 on every path.

**Self-validated:** `scripts/smoke.sh` → **25/25** (8 M0 + 4 M1·a + 6 M1·b + 7 M2), stable across repeated
runs. The 7 M2 checks use an embedded **stdlib RFC6455 WebSocket client** (no `websocat`/`wscat`/`websockets`
dependency — none guaranteed on a host): non-WS GET → 400 `bad_request` envelope; per-server `metrics.tick`
carries the stub values verbatim (`cpuPctCore`>100, null `ioWriteBps`); `host.metrics` capacity present;
the `servers` topic stays **quiet** under the metric firehose (status stable → zero `server.patch`); then,
killing the stub mid-stream, the **degrade** flip (`capabilities.patch` metrics `down`, `provisioned:true`)
+ metric-tick **silence** during the outage; then, restarting the stub, the **recover** flip (metrics back
`operational`, `provisioned:true` on every patch throughout) + ticks **resume**. A capability warm-wait
makes the cold-start (`unknown`) deterministic.

**Coverage honesty (what is NOT exercised):** (1) `server.patch`/`server.removed` **emission is
code-path-only** — the smoke roster is static and status stable, so the `DomainPump` diff never fires a
patch; check 22 proves only the *negative* (no spurious emit under ticking metrics, i.e. `CoreChanged`
returning false). Their *data shape* (`Server`) is REST-proven (M1·b checks 13–18, same record + JSON
options); a live status change needs an M3-shaped mutation (deferred) or the planned `tests/` project.
(2) The capability flips are proven against the **stub** monitor's `/health` (200-on-all-paths); the real
monitor now serves `/health` too (unified 2026-06-15), so a live exercise no longer awaits an upstream rename.
(3) `degraded` status is
**reserved/unused** (a restarting leaf is `down`); `since` is observed-time, not leaf-authoritative.
(4) The §3·j client-side resilience (poll-fallback, backoff, re-hydrate) is the frontend's half — the
backend provides the silence/flip signals it keys off, not the reconnect logic.

**Owed to the frontend at the gate:** subscribe to the topics the active page needs (unsubscribe on
navigation); treat `server.patch` data as the full honest `Server` element (merge by id), `server.removed`
as a drop; render a `capabilities.patch` `down` (with `provisioned:true`) as "temporarily unavailable",
not "gone", and snap back on the `operational` flip; on socket drop, fall back to REST polling and
re-hydrate on reconnect (§3·j). The stream is **unauthenticated until M4** — same trusted-network window
as M3.

### M3 — 2026-06-15 · commands (the first write path: gate → job → verify) self-validated; frontend gate PENDING

**Status:** the first mutation path is built and self-validated. `POST /servers/{id}/commands { verb }`
admits a closed verb set (**`start`·`stop`·`restart`**; `update` deferred), creates an in-memory `job`,
runs the verb off-request through kgsm-lib `ILifecycleService` (native→watchdog, container→Docker), and
streams the job on the `jobs` WS topic as a single coalesced `job.patch`, then a verify `server.patch` on
settle. Contract frozen in §6; `job.state` is the **job's own** lifecycle (the honest divergence from the
§5·d server-shaped example, signed off like the M1·b DTO).

**Decisions (user, 2026-06-15).** (1) **Trust window CONFIRMED** — trusted LAN only, not publicly exposed
until M4, so the unauthenticated write path is acceptable for this milestone (permissions land at M4).
(2) **Verb scope = three fast run-state verbs**; `update` (long-running, version-changing) follows once the
job-progress story is proven.

**The shape of it.** `CommandGate` (pure) rejects only the obvious no-ops against the *real observed* status
— start-when-running / stop-when-stopped — and lets everything else through so the **engine stays the single
authority** on what a verb does (a subtler-but-impossible transition runs and surfaces as the job's `failed`
+ kgsm's real error; `unknown` status never blocks). `JobRegistry` (singleton, in-memory) holds job state and
enforces **one in-flight command per server** via an atomic slot claim (second concurrent command → `409` +
the in-flight job id). `CommandRunner` (singleton) executes on a background task that creates its **own DI
scope** and resolves the transient/process-based `ILifecycleService` *there* — the request scope is gone by
`202`, so capturing a request-scoped service would be use-after-dispose; only the `Job` (value data) crosses
the boundary. The verb runs in try/finally so a started job **always** settles (releasing the slot even on a
throw). On settle it re-reads run-state and publishes a `server.patch` (the §5·d `command.verified` for the
direct write path); the `DomainPump`'s next diff also reconciles (coalesced by the same key, so the overlap
is harmless).

**Self-validated:** `scripts/smoke.sh` → **28/28** (8 M0 + 4 M1·a + 6 M1·b + 7 M2 + **3 M3**), stable. The 3
M3 checks prove the **gate/rejection contract without any mutation** (the gate rejects before a verb runs):
`400 bad_request` (unknown verb) · `404 not_found` (unknown server) · `409 conflict` (a no-op chosen from the
server's live status — `stop` on the stopped `factorio-test`). All three use the frozen `{error:{code,message}}`
envelope, not ProblemDetails.

**Live-validated on the trusted host (2026-06-15)** — the happy path the stub-driven smoke can't reach
(a real verb mutates real state) was exercised directly against the dev kgsm + `factorio-test`: `POST start`
→ **`202` + `{ job }`**; the `jobs` WS streamed the **`job.patch` lifecycle `running→succeeded`**; a **verify
`server.patch`** landed on the `servers` topic on settle; and **6 concurrent `POST start`s returned exactly
one `202` + five `409 conflict`** — the atomic one-in-flight-per-server guard, proven under true concurrency.
(Asserted the *mechanics*, not the verb's outcome.)

**Environment finding (not an M3 issue):** the test host had **no `kgsm-watchdog` running**, so kgsm-lib's
`Start` fell back to **direct-spawn**, producing an **orphaned** `factorio` process (PPID 1) that `kgsm stop`
reported "stopped" for but did **not** reap (and that the run-state read reported as `stopped` while it was
in fact up). The M3 verify path worked correctly — it published the `server.patch` kgsm gave it; the *status
value* was wrong because kgsm's native run-state tracking is unreliable **without the watchdog**. Cleaned up
by killing the orphan directly. Takeaway: M3's real-lifecycle correctness (accurate verify status, reliable
stop) depends on the watchdog being up — the engine's resident half, assumed present per the keystone.

**Owed to the frontend at the gate:** issue intent only (`{ verb }`), never a result; on `202` track the
returned `job.id` on the `jobs` topic (`job.patch`, merge by id); show the optimistic transitional state
derived from the verb until a `server.patch` carries the authoritative status; render `409` as "already in
that state / a command is already running", `400` as an invalid action. Unauthenticated until M4 — trusted
window as above.

### M4·a — 2026-06-15 · auth (Discord per-host, Model A — the credential-independent half) self-validated; frontend gate PENDING (M4·b now live-validated — see the M4·b entry below)

**Status:** the security boundary is built and self-validated for everything that does NOT need a live
Discord app. Auth is **on by default**; the M0–M3 contracts now run behind it. The live OAuth round-trip
(M4·b) and the collaborative session-state-machine gate are deferred — no Discord creds + no frontend yet.

**Decisions (this session).** (1) **Bearer = stateless JWT** (the §5-open call) — HMAC-SHA256, access ~15 min
+ refresh with an 8h absolute cap; no session table, no user row (honors §3·f "no user row anywhere", keeps M5
as the first EF migration; trade = no instant server-side revocation, bounded by the short TTL). (2) **Build the
credential-independent half now**, defer live validation (the M1/M3 split). (3) **Auth ON by default** with an
explicit, loudly-logged `KGSM_API_AUTH_DISABLED=1` escape hatch (synthetic admin — the pre-M4 open window).

**The shape of it.** Everything that talks to discord.com is behind one seam, `IDiscordIdentityResolver`
(code→identity→tier), so the whole authorization surface is testable in-process with a fake — the same
"exercise the contract without the live dependency" move that made the M3 gate testable without mutation.
`SessionTokenService` mints/validates the host-scoped JWTs and shares its `TokenValidationParameters` with the
JwtBearer pipeline (access and refresh validate identically); `OnTokenValidated` rejects a refresh token used
as an access bearer; `OnMessageReceived` reads `?access_token=` for the `/stream` WS (a handshake can't set a
header). Hierarchical tier policies (`TierRequirement`: viewer⊆operator⊆admin) gate the endpoints — viewer on
Hosts/Servers reads + the stream, operator on the command `POST`; an unauthenticated caller fails to a `401`
challenge, an authenticated-but-too-low tier to `403` (the framework picks challenge vs forbid), both rendered
as the frozen `{error}` envelope. **Honest failure modes (the security analog of never-fabricate-a-status):**
Discord unreachable → `502`, never a default grant; `none`/not-in-guild → terminal `403`; a failed role lookup
is never silently downgraded.

**Role resolution is via the bot token, by doc mandate.** The session scopes are `identify guilds`
(`architecture.html:570`) — `guilds` lists guilds, not roles, and `guilds.members.read` is absent, so the user
token *cannot* yield roles. `GET /guilds/{guild}/members/{user}` with the **bot token** is the only path. The
API holds its own copy of the Discord app/guild/bot-token/role-map — **shared external config** (the same values
the host's Discord bot uses), explicitly NOT a process dependency on kgsm-bot (keystone §4 lines 141–143).

**Self-validated:** `scripts/smoke.sh` → **31/31** (8 M0 + 4 M1·a + 6 M1·b + 7 M2 + 3 M3 + **3 M4·a**). The M0–M3
checks run under `KGSM_API_AUTH_DISABLED=1` (synthetic admin) so they exercise the domain contracts unchanged; a
dedicated **auth-ENABLED** instance then proves the no-token sweep: protected endpoints (`/hosts`, `/servers`,
`/stream`, the command `POST`) → `401` + the frozen `{error:{code:unauthorized}}` envelope; `/health` + `/api/v1`
stay open (the SPA's pre-login reachability probes); the login endpoint → `503 auth_unconfigured` (the M4·b half).
**`tests/Api.Tests` → 30/30** (xUnit + `WebApplicationFactory`, the Discord seam faked — the first test project,
stood up here): the full **401/403/viewer-operator-admin matrix**, the none-tier / refresh-as-access /
wrong-signature / garbage-token rejections, the **WS `?access_token=` path** (connects with a viewer token, the
handshake fails without one), and the callback verdict (`ok`/`denied`/`invalid`/`upstream-error`) + refresh
rotation + session snapshot — all deterministic, no Discord.

**Coverage honesty (what is NOT exercised — the M4·b live half).** The **real** `DiscordIdentityResolver` (the
actual code exchange + `/users/@me` + bot-token guild-member lookup over HTTP) is **built but un-live-validated**:
the login endpoints `503` until `KGSM_API_AUTH_DISCORD_*` are supplied. The tests/smoke prove the pipeline,
policies, token machinery and verdict *logic* with the seam faked; the wire correctness of the three Discord
calls is the M3-style **live validation on the trusted host**, owed once the user provides the Discord
application (client id/secret + a registered redirect URI), the bot token, the guild id, and the role→tier map.
Also deferred to M4·b live hardening: the OAuth `state` CSRF round-trip validation (today `/start` generates a
state but the callback doesn't yet verify it — stateless, so it needs a signed-state or cookie mechanism).

**§6 divergences recorded (the negotiated honest-vs-aspirational call, like the M1·b DTO / M3 job.state):**
camelCase `userId` (not `user_id`); `GET /auth/session` returns the **login-time profile snapshot** (not a live
re-fetch — the Discord token is discarded per §3·f, so "fetched live" can't hold); role re-check only at a full
bounce (≤8h cap), not on refresh; `/auth/session/refresh` reads the refresh token from the `Authorization`
header (the `{host}` body is accepted but unused).

**Secure-by-default (closed a gap at review).** Every endpoint is now gated unless it explicitly opts out: an
authorization `FallbackPolicy` requires an authenticated caller, `/health` + `/api/v1` carry `[AllowAnonymous]`
(the SPA's pre-login reachability probes), and the diagnostics probes (`_throw`/`_dbcheck`) are **admin-gated**
(a review caught them unauthenticated — `_dbcheck` touches the DB). Smoke's no-token sweep now asserts
`_dbcheck`/`_throw` → `401` too, so "protect all prior endpoints" holds with no open back door. The M0 `_dbcheck`/
`_throw` checks still pass because they run under the disabled escape hatch (synthetic admin clears the gate).

**Owed to the frontend at the gate:** the per-host session state machine end-to-end (`none`→`bootstrapping`→
`live`/`denied`/`login_required`); `401` recoverable (refresh, then silent re-bounce past the 8h cap), `403`
terminal (never auto-re-auth); proactive refresh at ~75% of the access TTL; the bearer in `sessionStorage` (per
host, never disk) + a URL-only host registry in `localStorage`. Tier-gated controls (hide/disable the command
buttons below `operator`).

### M4·b — 2026-06-15 · auth (the live Discord OAuth round-trip) LIVE-VALIDATED on the trusted host; frontend gate PENDING

**Status:** the one thing M4·a couldn't prove — the wire correctness of the three Discord calls — is now
proven against the real `discord.com`. The credentials arrived (a real Discord app + bot token + guild +
role→tier map, held in a gitignored `appsettings.Development.json`), so the backend half of M4 is **built**.
Only the **frontend session-state-machine gate** remains before the full M4 milestone flips to `built`.

**Config-load trap (caught first).** `ApiOptions` reads via `IConfiguration`, and `appsettings.Development.json`
only layers in when the environment is `Development` — a bare `dotnet run`/`dotnet <dll>` defaults to
**Production** (there is no `launchSettings.json`) and would silently ignore the file (login endpoints keep
`503`-ing as if unconfigured). Run with `ASPNETCORE_ENVIRONMENT=Development`. Confirmed: `/auth/discord/start`
→ `302` with a well-formed authorize URL (right `client_id`/`redirect_uri`/`scope=identify guilds`/`state`),
`/auth/discord/callback` (no code) → `400` (not `503`), i.e. `DiscordConfigured` true.

**OAuth `state` CSRF round-trip — built here (the one piece of M4·a-owed logic).** `/start` sets a one-time
HttpOnly `state` cookie (`kgsm_oauth_state`); `/callback` requires the echoed `state` to equal the cookie
(constant-time compare) *before* any Discord exchange, else `400 invalid_state`; the cookie is cleared on use
(no replay). **Stateless** — a client-side cookie, no server store, honoring the no-session-table decision.
`Secure = Request.IsHttps` (off on the http loopback dev host, on under https); `SameSite=Lax` so it rides
Discord's top-level cross-site redirect back (Strict would drop it); `Path=/auth/discord`; 10-min TTL. Observed
live: `Set-Cookie: kgsm_oauth_state=…; max-age=600; path=/auth/discord; samesite=lax; httponly`. Fake-tested:
the existing callback-verdict tests now each drive a real `/start`→`/callback` round-trip (proving the cookie
carries end-to-end), plus two negatives (state mismatch, no-cookie → `400 invalid_state`). **tests 32/32**
(was 30; +2), **smoke 31/31** unchanged (smoke's login check runs Discord-unconfigured → `503` before the
cookie path).

**Live round-trip (real browser, real Discord).** A real login through `/auth/discord/start?prompt=consent`
resolved end-to-end: code exchange → `/users/@me` → `GET /guilds/{guild}/members/{user}` with the **bot token**.
The resolver logged `Discord auth resolved user 245717107596197888 on guild 385730677141929985 -> tier admin
(3 roles)` — i.e. the bot-token role lookup found the member's 3 guild roles and mapped them to `admin`. The
callback returned `{ verdict:"ok", tier:"admin", token, refresh, userId:"discord:245717107596197888" }`, and the
`state` matched the cookie (the CSRF gate passed live, not just in tests). Host audience resolved to the machine
name (`hotrod`, `KGSM_API_HOST_ID` blank).

**Live bearer verification (the minted token against the running pipeline).** admin `GET /hosts` → `200`; admin
command `POST` (no such server) → `404` (past the operator gate); admin `GET /_dbcheck` (admin-gated) → `200`;
no-token `/_dbcheck` → `401` (secure-by-default); the **refresh token used as an access bearer** → `401` (the
`tkn` split holds live); `GET /auth/session` → the login-time profile snapshot (`heisen9386`/`Heisen`,
scopes `identify guilds`); `POST /auth/session/refresh` (refresh bearer) → a fresh access token that then
`GET /hosts` → `200` (rotation works). All as designed.

**Honest boundary / op notes.** Dev ran with `KGSM_API_AUTH_SIGNING_KEY` **blank → an ephemeral key** (logged
loudly; tokens die on restart) — set a stable secret on any real host. The role lookup needs the bot to be a
**member of the guild** (it is); a not-in-guild member would yield `denied/403`. `prompt=none` (the `/start`
default, silent SSO) bounces back `login_required` on a first-ever consent — use `?prompt=consent` for the
first login (the frontend retries with consent per §3·f). Discord error-param handling on the callback
(`?error=access_denied`) currently falls through to `400 bad_request` (no code) — a minor frontend-gate polish,
not a security gap. **Reverse-proxy note (forward, non-blocking):** `Secure` on the state cookie tracks
`Request.IsHttps`, which reads `false` behind a TLS-terminating proxy unless `UseForwardedHeaders` honors
`X-Forwarded-Proto` — wire that when a real https-behind-proxy deploy lands so the CSRF cookie keeps its `Secure`
flag (low severity: bearers ride the `Authorization` header, not cookies; the nonce is the only cookie).

**Owed before the full M4 flips to `built`:** only the **frontend gate** (the per-host session state machine +
tier-gated controls) — the SPA, still `planned`. The backend auth boundary is complete and live-proven.

### M5 — 2026-06-15 · audit log (the append-only event-persistence consumer) self-validated; frontend gate PENDING · live socket round-trip DISCHARGED at M6·0 (real `instance_started`→`server.start` row, 2026-06-16)

**Status:** the durable, append-only action record is built and self-validated — persistence **downstream of
the stateless engine** (CLAUDE.md invariant #5), exactly where keystone O3 puts it. The frontend gate
(`auditStore` prepends on `audit.append`) and the live kgsm-socket round-trip are pending (the latter a
trusted-host validate, like M3's mutation happy path).

**The shape of it (frozen §6).** `GET /api/v1/audit?cursor=&limit=&severity=&serverId=&actor=` → keyset
`{ data, nextCursor }`, newest first (rowid `DESC`, `nextCursor` = last rowid or null on a short page);
`audit` WS topic carries `audit.append` (one full immutable record, coalesced by the **unique event id** so
distinct appends never supersede each other — the client prepends). The record is the §3·d shape with two
recorded divergences: **`origin` nullable** (a direct-CLI engine action has no surface → `null`, never a
fabricated one) and **no `meta.jobId`** (no correlation id round-trips the stateless engine → `meta` holds
action-specific detail instead).

**The source model (the no-double-write decision, the whole point of the upstream provenance work).** kgsm
**owns** `server.*`/`backup.*`; the API records the engine's **event echo** rather than writing an audit row
when it issues a command. The command path (`ServersController` → `CommandRunner` → `ILifecycleService
.Start/Stop/Restart(serverId, actor, origin)`, the kgsm-lib 1.8.0 hook) **stamps** the bearer identity
(`discord:<username>`) + the caller-declared surface; kgsm emits them on the event; `KgsmAuditConsumer` reads
them back and writes one row. So watchdog-driven (`origin=system`) and direct-CLI actions audit uniformly
through the same path, and there is no echo-dedup problem (which is unsolvable without provenance). `auth.*`
has no kgsm event → `AuthController` writes it directly (no double-write risk). **actor and origin stay
independent axes** — origin is never derived from the actor (the explicit user requirement); a missing/unknown
origin is `null`, not guessed.

**Actor fidelity (the mapping risk, tested via round-trip).** The flat event `Actor` string is `provider:name`;
`AuditMapping.ParseActor` derives `kind` from the provider (`discord`→user, `api`→token, `system`→system),
treats a bare string as kgsm's OS-user fallback (`user`/`system`), and leaves `provider` null for an
unrecognized prefix rather than coerce it. The test asserts the **round-trip**: what the command path stamps
(`discord:haru`) parses back to `{kind:user, name:haru, provider:discord}`. **No compound-emission
double-count (verified in kgsm):** `_cmd_restart` dispatches exactly **one** `instance_restarted` event —
`__logic_instance_restart` composes the stop/start *logic* functions internally (they only return exit
codes; the command layer emits once on the single terminal code), so a restart is one `server.restart`
row, never a stop+start+restart triple. (Install likewise maps only its terminal event, skipping the
download/deploy sub-steps.)

**No EF migration (user directive 2026-06-15).** Greenfield/dev authority: the schema is `EnsureCreated`, and a
schema change means wiping the dev DB — there is no `__EFMigrationsHistory`. The M0 `Probe` table is **removed**
(replaced by `AuditEntry`); `_dbcheck` became a **read** round-trip (the append-only table must never be
probe-written). The consumer `EnsureCreated`s at startup so `GET /audit` + the auth writes work even with no
engine. (The advisor's EnsureCreated trap — it no-ops on an existing DB — is handled: smoke `rm -f`s its DB,
and the dev box had no stale DB; verified the `audit` table lands by `_dbcheck` → `auditRows:0` and a real
`GET /audit` query, not assumed from a green build.)

**Socket direction (verified, not assumed).** kgsm-lib's `UnixSocketClient` **binds + listens**; kgsm
**connects outbound** (`socat - UNIX-CONNECT:`) to push each event, and only to a socket file that already
exists (the `config_event_socket_filenames` multi-listener list). So the api must bind its **own dedicated**
`KGSM_API_KGSM_SOCKET` first (the listener deletes any file at its path before binding — a shared default
could clobber another consumer's live socket; smoke uses a temp path to avoid this). Events emitted while the
api isn't listening are not backfilled — the honest downstream-consumer boundary.

**Self-validated:** `scripts/smoke.sh` → **33/33** (8 M0 + 4 M1·a + 6 M1·b + 7 M2 + 3 M3 + **2 M5** + 3 M4·a);
the 2 M5 checks prove the `GET /audit` empty `{data:[],nextCursor:null}` page + the filter params (the table
existing is itself the proof EnsureCreated ran), and the no-token sweep now includes `/audit` → `401`.
**`tests/Api.Tests` → 59/59** (+27 since M4·b): `AuditMappingTests` (the actor round-trip + provider→kind
derivation, origin normalization, event→write, the entity↔record + meta round-trip) and `AuditTests` (keyset
newest-first + the honest record shape, the two-page pagination walk, the severity/actor filters, the viewer
`401`, the **`auth.login` written-and-read end-to-end** through the fake Discord callback, and the `audit` WS
**`audit.append`** delivery). Release build **0-warning** (the analyzer gate).

**Owed to the frontend at the gate:** `auditStore` prepends on `audit.append` (immutable — a correction is a
new event, never an edit); the filters map 1:1 to indexed columns; render a `null` origin honestly (no surface
declared, e.g. a CLI action) rather than inventing one; on (re)connect hydrate via `GET /audit` and apply
appends (the §3·j pattern — the WS is patch-only, no replay).

### M6·0 — 2026-06-16 · kgsm-lib bump (1.8.0→1.13.0) + audit-consumer extension (crash + ports) self-validated + LIVE-VALIDATED on the trusted host (discharges M5's owed socket round-trip)

**Status:** the first M6 increment — internal-only (no wire contract → no frontend gate), discharging M5's
owed audit debt now that the producers landed. It bumps kgsm-lib **1.8.0 → 1.13.0** and extends the M5 audit
consumer with the two newly-sourceable action families. Picks up `IFirewallService` (1.11.0) + the firewall
port events (1.12.0) + the watchdog crash events (1.9.0) that M6·b/M6·a will build on.

**The bump, proven behaviour-neutral in isolation (the de-risking discipline).** The version was bumped and
**restore + 0-warning Release build + tests 59/59 + smoke 33/33** were confirmed green *before* a line of new
code, so any break is the bump or the new code, never both. Verified clean over the 1.9→1.13 breaks: the api
reads **none** of the removed surfaces — no `Instance.Ports`/`ConfigurationInfo.Ports`/`Instance.UpnpPorts`
(the 1.10.0 structured-port cutover) and no `IFileService` (the 1.13.0 `CreateUfw`→`CreateFirewall` rename).
The transitive `TheKrystalShip.KGSM.Firewall.Contracts` 1.0.0 restored cleanly from the local feed.

**The audit extension (the pure-mapper pattern, kept testable).** `server.crash` (both `InstanceCrashedData`
→ warn "auto-restarting" and `InstanceFailedData` → danger "supervisor gave up after N restart(s)", the single
doc-given action distinguished by severity + summary + the restart count in `meta:{exitCode,restarts}`) and
`network.ports.open`/`network.ports.close` (the CLI-path `instance_ports_opened`/`_closed` echoes →
`meta:{ports}` in the canonical range-preserving form, e.g. `2456-2458/udp, 27015/tcp`). Per-event policy lives
in new **pure** `AuditMapping` mappers (`FromCrashEvent`/`FromFailedEvent`/`FromPortsOpenedEvent`/
`FromPortsClosedEvent` + a public `FormatPorts`) — unit-tested without a live socket, exactly where M5 put
`FromServerEvent`; `KgsmAuditConsumer` adds four one-line `RegisterHandler` wires. Crash events are
`system`/`system`-stamped upstream, which the existing `ParseActor`/`NormalizeOrigin` handle unchanged.

**The two structural calls (both never-fabricate consequences).** (1) **`open_ports` will audit via a direct
write at M6·b, not a kgsm echo** — the api calls the firewall authority *directly* (kgsm runs nothing, no echo
exists), structurally the `auth.*` case; the **CLI path stays event-sourced** (kgsm bash emits → this consumer
audits). Disjoint, both honest, no double-write, no circular self-emit. (2) **`network.ports.close` is a
server-side additive action** beyond the doc's `ports.open`-only `network` set — recorded because it is now
honestly sourceable (`instance_ports_closed`) and keeps the trail symmetric (a standalone `files firewall
disable` would otherwise leave an opened-never-closed gap); the frontend accepts unknown actions forward-compat.
It is cleanly CLI-echo-only (no api close command — §3·g is open-only), so it carries no double-write risk.

**Self-validated:** 0-warning Release build; **tests/Api.Tests → 67/67** (+8: crash warn/danger mapping incl.
the empty-restarts defensive case, the `system` provenance passthrough, ports open → `network.ports.open` and
close → `network.ports.close` with the formatted-ports meta, and the `FormatPorts` range/single/empty theory);
**smoke 33/33** unchanged (no wire surface changed). Confirmed the four new event-type strings
(`instance_crashed`/`instance_failed`/`instance_ports_opened`/`_closed`) are in kgsm-lib's `EventService`
dispatch map **and** `KgsmJsonContext` — so `RegisterHandler<T>` actually fires (closing the silent-zero-rows
failure mode).

**LIVE-VALIDATED (2026-06-16) — the real kgsm→socket→audit round-trip, which also discharges M5's still-owed
socket round-trip.** Ran the api (auth-disabled, engine at the dev kgsm, a dedicated `KGSM_API_KGSM_SOCKET` =
`$KGSM_ROOT/kgsm.sock`, already in kgsm's `event_socket_filenames` with `enable_event_broadcasting=true`),
confirmed it bound the listener, then drove **four** real engine actions and read the rows back off `GET /audit`:
- **`network.ports.open`** — a real `files firewall enable factorio-test` (via the deployed kgsm-firewall
  authority) → `instance_ports_opened` → row with `meta.ports = "34197/tcp, 34197/udp"` (matching the
  instance's configured ports), `origin:null` (CLI, no surface — honest), `actor:{user,heisen,system}`.
- **`network.ports.close`** — a real `files firewall disable` → `instance_ports_closed` → the symmetric
  `network.ports.close` row (and it cleaned up the ufw rule the open created).
- **`server.crash`** (warn) — started factorio-test under the watchdog, `kill -9`'d the cgroup leader; the
  watchdog's `CrashWatcher` detected the death, auto-restarted, and emitted `instance_crashed` → the row landed
  with `origin:"system"`, `actor:{system,system,system}`, `meta:{exitCode:"137",restarts:"1"}` (137 = SIGKILL),
  summary "factorio-test crashed — auto-restarting". Proves the watchdog's `system`-stamped emit decodes and
  maps correctly. (The `kgsm-firewall` socket is `root:kgsm 0660`, so the firewall ops ran under `sg kgsm`;
  the watchdog shells the same canonical `KGSM_WATCHDOG_KGSM_PATH=$KGSM_ROOT/kgsm.sh`, so its emit hit the
  same socket.)
- **`server.start`** (bonus) — the `kgsm start` above also produced an `instance_started` → `server.start`
  row, so the **M5 lifecycle path is now live-proven too** (M5 had only the empty-page + tests; the live
  socket round-trip it owed is hereby discharged).
Newest-first keyset ordering held across all four. Cleaned up after (stopped the instance, removed the socket +
temp DB; the kgsm config was untouched — broadcasting was already on). **Still code-path-only:** `instance_failed`
→ `server.crash` **danger** (it needs the watchdog to *exhaust* its restart retries — repeated kills, too
disruptive to force here); the `FromFailedEvent` mapper is unit-tested, and it rides the same now-live-proven
transport + dispatch as the warn crash.

### M6·b — 2026-06-16 · ports (the network surface: required ⋈ open + the open_ports command) self-validated + LIVE-VALIDATED end-to-end with ufw active (open:true enforced, direct audit, app-join, network.patch delivery, restore — 8/8); frontend gate PENDING; the kgsm-firewall enforcement-state follow-up is now BUILT + LIVE-VALIDATED 2026-06-16 (inactive ufw reads reachable, not closed — see the enforcement-axis subsection at the end)

**Status:** the ports half of M6. Contract **frozen 2026-06-16** (§6 `network`-block row + the three locked
decisions); backend built, self-proven, and the firewall **read** path live-validated against the deployed
kgsm-firewall daemon. Owed (like M3's mutation / M5's append): the live `open_ports` **mutation** round-trip
(it opens a real host firewall port) + the frontend gate.

**No kgsm-lib bump** (1.13.0 from M6·0 already carries `IFirewallService`). The firewall is **opt-in like the
assistant** (`KGSM_API_FIREWALL_SOCKET` blank ⇒ `absent`), and deliberately **NOT** in the 2s `LeafHealthMonitor`
poll — kgsm-firewall is socket-activated + idle-exits, so it is probed **on-demand** (detail views + the
open_ports verify), each call bounded (2s read / 30s mutate), liveness reported as the block-level `firewall`
status. Confirmed live that `GetAll` (`instances list --detailed --json`) carries the structured `ports`, so
`required` needs no extra spawn (the §4 fact-check the freeze owed).

**The three frozen decisions, as built.** (1) **`reachable` reserved → always `null`** (no upstream prober;
the honest verdict is per-row `open` = host-firewall-rule-present, the frontend derives "all open" itself —
rename-not-redefine, the M1·b `cpuPctCore` precedent). (2) **Dedicated `servers/{id}/network` WS topic**
(`network.patch`) for the verify push, so `server.patch` stays the frozen M1·b `Server` (detail ≠ list — the
first such split; the list/stream omit `network` via `JsonIgnore(WhenWritingNull)`, byte-identical to M1·b).
(3) **Block-level `firewall` status only** — `HostCapabilities` unchanged, no redundant polled leaf.

**Honest-unknown, never fabricated-closed (the central M6·b discipline).** `required[]` is always present
(domain truth from `Instance.Ports`, firewall-independent); per-row `open` and `reachable` go **`null`** — never
`false` — when the firewall can't answer; the host grid is **`null`** when unreachable/unknown, **`[]`** only on
a real `Ok`-but-empty (the `ListOwnedAsync` `Unknown`≠empty distinction, preserved end-to-end). `NetworkAggregator`
maps `FirewallException`→`down`, `ListOwnedAsync.Status` `Unknown`→`unknown` / `Unsupported`→`unsupported` /
`Ok`→`operational`, all on-demand and bounded.

**The `open_ports` command (intent-only, server-derived, direct-write).** Added to the closed `CommandVerb` set;
always admissible (no run-state no-op — declarative/idempotent), shares the one-in-flight slot. `CommandRunner`
branches: it derives the target from the instance's own `Instance.Ports` (never a client list), calls
`IFirewallService.EnsureOpenAsync`, and on a real change (`Applied`) writes the `network.ports.open` audit row
**directly** — kgsm runs nothing on the `IFirewallService` path, so there is no echo and **no double-write** (the
CLI echo path is disjoint, M6·0). A `NoOp` succeeds without a row (recording "opened" when nothing changed would
fabricate a change — symmetric with the CLI echo, which only fires on a confirmed open). The audit `meta` carries
`{jobId, ports}` — **`jobId` IS populatable here** (the api owns both the job and the append; the M5 "no jobId"
limit was the event-echo path), giving the alert↔audit `resolution.actionId` bridge M6·a needs. On settle the
verify re-probes and pushes the fresh block on `servers/{id}/network` (byte-identical to a `GET /servers/{id}`
network field — one shared build path).

**Self-validated:** 0-warning Release build; **tests/Api.Tests → 79/79** (+12: `NetworkAggregator` cross-ref —
absent→open:null, operational open true/false, **Unknown→open:null not false**, unreachable→down, unsupported,
range-expansion, the host grid Ok-rows / **Ok-empty→[] / Unknown→null** distinction; + `FromPortsOpenedCommand`
direct-write with the jobId correlation + the no-ports case); **smoke 33/33 → 37/37** (+4 M6·b degrade path:
`open_ports` is an admitted verb (unknown server→404 not 400), the server detail `network` block reports
`firewall:"absent"` + `reachable:null` + every `open:null`, the `/servers` list **omits** `network` (detail≠list,
M1·b shape preserved), the host grid is omitted when the firewall is absent).

**LIVE-VALIDATED (2026-06-16) against the deployed kgsm-firewall daemon — the api's full open_ports path as a
faithful firewall client; the end-to-end `open:true` verdict UNPROVEN (a daemon-side gap, flagged below).**
Ran the api under `sg kgsm` (the `/run/kgsm-firewall/firewall.sock` is `root:kgsm 0660`, same constraint as M6·0)
with `KGSM_API_FIREWALL_SOCKET` set, engine at the dev kgsm.

*Read path:* `GET /servers/factorio-test` → `network.firewall = "operational"` (the api reached the real daemon;
`ListOwnedAsync` returned `Ok`), `required = [34197/tcp, 34197/udp]` (from `Instance.Ports`), both `open:false`,
`reachable:null`; `GET /hosts/live-host` → `network.openPorts = []` (the **`Ok`-but-empty** grid — honest `[]`,
not `null`). **Honesty correction:** that `open:false`/`[]` is **not** "the daemon owns no rules" — it is the
daemon enumerating **nothing** (see the daemon gap below); do not read it as a confident measured-closed.

*Mutation round-trip (the live `open_ports`):* `POST /servers/factorio-test/commands {verb:"open_ports",origin:"api"}`
→ `202` + `job_…`; the runner server-derived the target from `Instance.Ports` (audit `meta.ports` =
`"34197/tcp, 34197/udp"`, no client list), called `IFirewallService.EnsureOpenAsync` → the daemon returned
**`Applied`** → the runner wrote the **direct `network.ports.open` audit row** with `meta.jobId == job_…` +
`meta.ports` + `origin:"api"` (read back off `GET /audit`); the **job lifecycle `running→succeeded`** and a
**`network.patch` frame delivered on `servers/factorio-test/network`** to a subscribed WS client; on settle the
verify re-probed and the runner **honestly reported `open:false`** (it did **not** assume `open:true` from
`Applied` — never-fabricate working as designed); `files firewall disable` restored cleanly. So the api's
write→audit→job→deliver→re-probe→restore path is **live-proven** (the `RunOpenPortsAsync` write-decision paths —
`Applied`→direct-write, the target derivation, the verify/`network.patch` — all executed).

**DEFINITIVE end-to-end test — PASSED 8/8 with ufw ACTIVE (2026-06-16, the user enabled ufw for it).** Re-ran the
full round-trip with ufw enforcing (default-deny incoming, SSH allowed): PRE `open:false` (active, no rule yet);
`POST open_ports {origin:"ui"}` → `202`+job → re-probe **`open:true` for every required port** (enforced by the
real ufw rule) → the direct `network.ports.open` audit row (`meta.jobId==job` + `ports` + `origin:"ui"`) → the
host grid lists `factorio-test` with **`app:"factorio"`** (the roster join, now proven) → job `running→succeeded`
+ a **`network.patch` frame carrying `open:true`** delivered to a subscribed WS client → `files firewall disable`
→ `open:false` again (rule removed, default-deny) → no leftover `ufw show added` rule. So the api's **entire
`open_ports` path is now proven end-to-end** (write→enforce→`open:true`→audit→app-join→WS-deliver→restore). ufw
left **active** (the user's setting); no leftover rule. The runner's `instances info <name>` port source was
confirmed to carry the same structured `ports` as the roster.

**Earlier-run honesty correction (the inactive-ufw observation that preceded this).** The first mutation run was
on an **inactive** ufw and showed `Applied` + `open:false` everywhere. The "produces no observable rule" read of
that was partly a grep artifact — the daemon adds an **application-profile** rule (`ufw allow kgsm-<instance>`),
NOT a numeric `34197/...` rule, so `ufw show added | grep 34197` could never match; combined with inactive
`ufw status` listing nothing, `ListOwnedAsync` enumerated empty. The rule was **staged** (persisted for when ufw
is enabled), not absent. The real gap is a **kgsm-firewall composite-honesty** one: on an inactive ufw the daemon
reports `operational`+empty (→ api `open:false` = "closed"), but **inactive ufw enforces nothing, so the port is
actually OPEN/unfiltered** — the verdict is inverted. That is a **kgsm-firewall design follow-up** (announce the
enforcement state: inactive → reachable/`open:true` + a distinct "not enforcing" status; possibly an
`applied-inactive` apply outcome) — see the firewall memory + the design note. **Not an api defect:** the api
faithfully maps each daemon answer and re-probed after its own open rather than assuming `open:true` from
`Applied`, which is exactly what surfaced the gap.

**ENFORCEMENT-AXIS follow-up — BUILT + LIVE-VALIDATED 2026-06-16 (the inversion above is now fixed across 3 repos).**
The "inactive ufw → api `open:false`" inversion is resolved. An installed-but-inactive firewall filters nothing,
so every port is reachable — the honest verdict is `open:true` + a `firewall:"inactive"` flag, never `closed`.
**As built:** **Firewall.Contracts → 1.1.0** (additive `FirewallResponse.Enforcement` nullable string +
`Enforcements` tokens + `Outcomes.AppliedInactive`); the daemon's `UfwDriver` runs one extra `ufw status` after a
successful allow → `AppliedInactive` when inactive, and `ListOwnedAsync` carries `Enforcement` from the same
status line. **kgsm-lib → 1.14.0** (`FirewallOutcome.AppliedInactive`, `FirewallEnforcement` enum,
`FirewallListResult.Enforcement`). **kgsm-api** (this commit): `NetworkAggregator` treats `Ok` +
`Enforcement==Inactive` as `firewall:"inactive"` + every `required[].open = true` (and the host grid likewise),
falling back to the legacy enforcing/rule-present path on `Unknown` enforcement (pre-1.1.0 daemon compat);
`CommandRunner` audits `Applied` **or** `AppliedInactive` (the latter `enforced:false`); `AuditMapping` emits a
"staged firewall ports … (firewall inactive — enforces on enable)" summary + `meta.enforced:"false"`. The bundled
CLI's exit-code switch was also fixed (`applied-inactive` → exit 0, else it would abort a kgsm install on an
inactive-ufw host — caught in review, the shared-contract/two-implementers lesson). **Security-of-presentation:**
`open:true` under `inactive` = "no firewall protection", NOT "allowed by a rule" — a client must read `firewall`
alongside `open` (documented in the DTO XML + the §6 row). **Accepted asymmetry:** a CLI-issued inactive open
audits as `network.ports.open` ("opened") because bash sees only one exit code, while the api direct-write
distinguishes "staged" — not a bug under inactive=open, and adding bash precision is out-of-scope. **Tests:**
kgsm-api **79 → 83/83** (+4: inactive→all-open, `Ok`+`Unknown`→fallback, host inactive grid, the "staged" audit
summary), smoke **37/37**, 0-warn. **LIVE-VALIDATED both transitions, wire → api** (the validation the M6·b body
above said the inactive read still owed): daemon `list` `enforcement:inactive`/`enforcing`; daemon `ensure-open`
`applied-inactive`/`applied`; bundled CLI inactive open → **exit 0**; `GET /servers/factorio-test` →
`firewall:"inactive"` + both ports `open:true` (ufw off) → `firewall:"operational"` + rule-gated `open:true` (ufw
on); `GET /hosts/hotrod` → `firewall:"inactive"` + empty grid (ufw off) → `firewall:"operational"` + the full grid
with the `app:"factorio"` roster join (ufw on); the new daemon binary deployed to `/opt/kgsm-firewall`, all test
rules removed, ufw left **active** (as found). Full cross-repo detail in the kgsm-firewall + kgsm-api memories.

### M6·a — 2026-06-16 · alerts (the condition-mirror feed: crash source) self-validated + LIVE-VALIDATED (real watchdog crash→raise→probation-resolve→auto-heal); contract PROPOSED (sign-off pending)

**Status:** the alerts half of M6. **Crash-only** (user-chosen scope, 2026-06-16): the watchdog's
supervision state is the one honestly-sourceable condition today. Backend **built + self-validated**
(unit + smoke) **and LIVE-VALIDATED on the trusted host 2026-06-16** (below); the wire contract is **PROPOSED,
not yet frozen** — the §6 row carries the divergences that need the frontend sign-off (like M6·b's three
decisions). **No kgsm-lib bump** (1.14.0 from the M6·b follow-up already carries `IWatchdogClient`).

**What it is.** `GET /api/v1/alerts?status=firing|resolved&since=24h` → `{ data }` + the `alerts` WS topic
(`alert.raise` full record / `alert.resolve` `{id,resolution}` / `alert.retract` `{id}`). **Read-only** — no
complete/dismiss/PATCH (§3·c); the feed trends to empty and the durable record lives in `/audit`. In-memory,
viewer-gated, always-on (like `LeafHealthMonitor`, not subscriber-gated — REST must serve fresh truth).

**The crash source — poll-as-authority.** `AlertEngine` (singleton + hosted service) polls
`IWatchdogClient.ListAsync()` (kgsm-lib — the chokepoint, never a raw socket) every ~5s. `Desired="running"`
+ `Phase="restart-pending"` → firing `warn` (`crash:<serverId>`); `Phase="failed"` (supervisor gave up) →
`escalated:true` `danger` that **never auto-resolves**; `Restarts` → `attempts`. **The poll interval IS the
raise debounce** — a crash that recovers faster than a tick is never seen down, so it never fires (§3·c "don't
fire on a blip"); we deliberately do NOT event-fast-path a raise. **Resolve is probation-gated** (api owns a
30s dwell from the first clear observation, so a crash-loop never flaps); **escalation is mirrored** from the
watchdog's own circuit-break, not re-derived. A vanished instance (uninstalled) **retracts** (no rear-view);
the rear-view ages off at 24h. **Honest-unknown:** a failed watchdog poll **skips the tick** — the firing set
persists, never resolved/retracted on a blind cycle. **Rebuilds on restart** because the watchdog state is
queryable (not an unreplayable event); a fired-and-resolved-while-down transition is simply absent (it lives
in `/audit`). **Honest boundary:** the watchdog supervises **native** instances only — container crashes are
out of scope until a Docker event source exists.

**The alert↔audit bridge — and the limit a build-time grep surfaced.** `AlertEngine.NoteRecoveryAction` is
handed the `evt_` id of a `server.start`/`server.restart` audit row by `KgsmAuditConsumer` **after** the write
(the row's id must exist), so a crash that clears because an **operator/api** start|restart brought the server
back resolves with `resolution.actionId` = that row. **But the watchdog's AUTONOMOUS crash-restart emits NO
start/restart event** (verified against the kgsm-watchdog source: it emits only `instance_crashed`/`_failed` —
its respawn is not an audited action). So a **pure auto-heal resolves with `actionId` null** — honest, never a
fabricated link (the doc's `actionId:"evt_restart_mc"` presumes an audited restart we don't have today).
Bridging auto-heals would require auditing the watchdog's autonomous restart — a future kgsm-watchdog/kgsm-lib
change, out of M6·a's api-only scope. This was caught by an advisor review before any "the bridge works" claim
(the self-test called `NoteRecoveryAction` by hand; smoke has no watchdog — neither could catch a missing call).

> **Update (2026-06-17/18) — both halves landed; this limit is closed.** kgsm-watchdog `d4b453f` made the
> autonomous crash-restart emit `instance_restarted` (system/system) → a `server.restart` row, so a pure
> auto-heal now bridges (live-validated). The latent risk that a *dropped* recovery event could leave a stale
> "last start/restart ever" id to mislink a later crash is now closed by **episode-scoping**: `_lastStartAction`
> stashes the action's audit-row timestamp, and `AlertEngine.BuildResolution` honors it only when it
> post-dates that crash's raise (`action.At >= RaisedAt`) — so a stale prior-episode action (operator or
> system) resolves to honest `null`. Sound because lifecycle events fire at operation completion (server up),
> after the down-poll that raised. The boot-autostart (`instance_started` system/system) is audited but never
> bridged (`IsRecoveryAction`); episode-scoping would reject its pre-crash timestamp regardless.

**Contract divergences (PROPOSED — need frontend sign-off, like the M1·b DTO / M6·b's three decisions):**
(1) the record carries a **top-level `hostId`** (beyond the §3·c example's `anchor.hostId`) so the SPA filters
by host without a join (§4·d); (2) `GET /alerts` is an **unpaginated `{ data }`** (no cursor — the feed is
small and trends empty, unlike `/audit`'s `{data,nextCursor}`); (3) `AlertSource` reserves `host-monitor`/
`metrics`/`assistant` but **emits only `watchdog`** at M6·a (the others have no honest source yet — never
emitted, never fabricated); (4) `anchor.surface:"server"` is a best-effort deep-link hint (the frontend always
has `serverId`/`hostId` to route from); (5) `alert.resolve` carries `{id,resolution}` per the doc and the
client stamps `resolvedAt` (the authoritative `resolvedAt` is on the REST resolved record).

**Tests + build.** kgsm-api **83 → 93/93** (+10: `AlertEngineTests` — raise/warn, never-raise-on-running/stopped,
failed→danger-never-resolves, probation-gated resolve + bridge actionId, autonomous auto-heal → null actionId,
re-crash cancels resolve, vanished→retract, stop-cleared→null actionId, 24h age-off, new-crash-after-resolve is
distinct). **smoke 37 → 39/39** (+2: `/alerts` empty-feed degrade + the `?status=resolved&since=` window;
`/alerts` added to the no-token 401 sweep). Release **0-warn**.

**LIVE-VALIDATED 2026-06-16 (trusted host `hotrod`, api against the live `kgsm-watchdog` over its control
socket, auth disabled).** Started `factorio-test` under the watchdog (watchdog capability read `operational`;
`GET /alerts` empty while `phase:running`). Drove real crashes by `kill -9`-ing the cgroup leader, gated on
the watchdog's own `restarts` counter to **failure 4** (an 8s `restart-pending` backoff window — comfortably
> the 5s poll, and short of the cap of 5 so it stayed in the `warn`/auto-restart regime, never `failed`).
Observed end-to-end: the `crash:factorio-test` **`warn`** alert **raised** (`detail:"crashed (exit 137);
restart #4 in 8s"` — the watchdog's real reason; `attempts` tracked the restart streak 3→4 = the escalation
re-push), held firing across the crash cycle, then — once the watchdog auto-restarted it and it stayed up —
**probation-resolved** after the 30s dwell (raised `16:57:49` → resolved `16:58:44`, **no flap**) into the
rear-view with `resolution:{by:"system", source:"watchdog", reason:"Recovered — running and stable.",
actionId:null}`. **The `actionId:null` is the advisor-caught honest boundary proven live:** the watchdog
restarted it autonomously (no audited action exists), so the bridge links to `null` — never a fabricated id.
The escalated/`danger` path + the operator/api-recovery `actionId` path stay unit-proven (forcing `failed`
exhausts the watchdog's retries — too disruptive, the same call as M6·0 left `instance_failed`). Host restored:
`factorio-test` stopped (as found), the api stopped, temp DB/socket removed.

### M8·a — 2026-06-19 · library catalog (`GET /library`, a pure blueprint scrape) self-validated incl. a live 29-blueprint read; frontend gate pending

**Status:** the first M8 increment — the installable-game catalog, the catalog analog of M1·a's pure
host scrape (no leaf join, no mutation, no fabricated field). Backend **built + self-validated**; the
collaborative gate (frontend swaps `libraryStore` mock → real) is deferred with the M0–M7 gates (no
frontend access). Not marked "frontend validated."

**What's next / not-blocked finding.** After M7 (committed `44a0c2b`, 2026-06-19), every frontend gate
is blocked on the not-yet-built SPA, so the next *buildable* milestone is M8. Audit confirmed M8 is
**NOT upstream-blocked** — kgsm-lib already exposes the catalog (`IBlueprintService.ListDetailed`), the
install/uninstall verbs (`IInstanceService.Install`/`Uninstall`), and the install/uninstall audit echo
(`KgsmAuditConsumer` already maps `InstanceInstalled/UninstalledData`). So M8·a was built directly,
starting with the smallest real slice (the catalog read).

**The upstream piece — done twice (the user-corrected pivot to the root-cause fix).** The blueprint surface
emitted `Ports` only as the legacy UFW string (`"26900:26903/tcp|26900:26903/udp"`) — unlike `instances info
--json`, structured since the 1.10.0 cutover. **First cut (superseded):** kgsm-lib gained
`PortMappingExtensions.FromUfwSpec` to parse the string in C# at the chokepoint (1.16.0). The user correctly
observed the *right* fix is upstream — make kgsm emit the canonical shape on the blueprint surface too, not
re-parse it downstream. **Final (the root-cause fix):** **kgsm now emits the structured `[{start,end,protocol}]`
array on `blueprints … --json`** (commit `18cbf83`, reusing the existing `__ufw_ports_to_json` helper — native
ports from the UFW spec, container ports derived into the same spec, empty/malformed → `[]`), and
**`Blueprint.Ports` becomes `List<PortMapping>`** (kgsm-lib **1.17.0** BREAKING `fdb7ad5`, `FromUfwSpec`
removed). One ecosystem port type everywhere; no consumer parses a string. kgsm blueprint unit + e2e tests
assert the structured shape (incl. proto-less → tcp+udp); kgsm-lib `Blueprint`/`PortMapping` suites green
(36/36; the lone EventService socket flake is pre-existing); **kgsm-bot rebuilt clean** against the breaking
`Blueprint.Ports` (its `KgsmBlueprintService` field-copy is `List`=`List`). 1.17.0 packed to local-nuget.

**The honest `LibraryEntry` (frozen in §6).** `{ id, name, type, steamAppId?, clientSteamAppId?,
isSteamAccountRequired, ports[{start,end,proto}], specs{...}, cover, rawgSlug }`. The divergences ARE the
contract: **`cover` reserved-`null`** (the advisor-flagged never-fabricate boundary — RAWG cover resolution
is its own later increment because honesty bars a fuzzy `DisplayName`→RAWG match that would mis-attribute
the wrong game's art; resolve only from an exact key like `SteamAppId`→Steam CDN); `rawgSlug` reserved-`null`;
`name` falls back to `id` when uncurated (every blueprint's `Metadata` is null today — verified live — so
`name==id`, never guessed); `steamAppId`/`clientSteamAppId` honest-`null` for a non-Steam blueprint (not the
`Server` DTO's `"0"`); `specs` keys present but all-`null` today; `category` query reserved/inert.

**Built (2026-06-19):** `Contracts/LibraryDto.cs`, `Services/Library/LibraryAggregator.cs` (resolve
`IBlueprintService` per-request, degrade-to-empty + log-once — the `ServerAggregator` engine-is-base
pattern; map + sort by id + `q` filter), `Controllers/LibraryController.cs` (viewer). kgsm-lib ref → 1.17.0.

**Self-validated:** `scripts/smoke.sh` → **44/44** (+2 M8·a): a **live read of the real 29-blueprint dev
catalog** that proves the **whole bash→lib→api chain** — asserting the frozen key set, structured ports
straight from kgsm (no C# parse), steam null-honesty both ways (≥1 non-null + ≥1 null `steamAppId`), a real
multi-port range carried through (`saw_range`), and reserved cover/rawgSlug; plus the `q` filter
(`factorio`→matches, a no-match→`[]`) and `/library` in the no-token 401 sweep. **`tests/Api.Tests` → 120**
(+11 `LibraryTests`): the Blueprint→`LibraryEntry` mapping honesty (null-not-zero steam, id-fallback name,
curated-metadata override, structured-port projection, reserved cover/rawgSlug, the q filter, id ordering,
engine-unconfigured + read-failure degrade-to-empty) + the viewer/none/no-token gate through the real
pipeline. Release **0-warning**.

**Found at the gate — a build-environment fix (not M8·a).** A freshly-published advisory
(GHSA-2m69-gcr7-jv3q) on the SQLite **native lib** that EF Core pulls transitively
(`SQLitePCLRaw.lib.e_sqlite3` 2.1.11 — the latest published build; no patched version exists yet) tripped
`TreatWarningsAsErrors`, breaking *any* build of the repo (HEAD included). Cleared with a **scoped**
`NuGetAuditSuppress` for that one advisory (NOT a blanket `NuGetAudit` off) in `src/Api/Api.csproj` and
`tests/Api.Tests/Api.Tests.csproj`, documented inline. **To be deleted the moment a patched SQLitePCLRaw
bundle ships** (bump EF Core / add a patched override instead). Exposure is minimal — SQLite holds only
this host's local append-only audit log on a trusted single host (no untrusted SQL, no network DB).

**Owed to the frontend at the gate:** not a clean backing-flip — the game grid must fall back to its themed
`art` gradient (`cover` reserved-`null`), render `name==id` until metadata curation lands, and treat `specs`
as all-null today. **Owed next:** M8·b (install `POST /servers` + the install job + uninstall) — the
`Install` `KgsmResult`'s new-instance-id surfacing for the job→server handoff is in-api plumbing to confirm,
not upstream.

### M8·b — 2026-06-19 · install + uninstall (`POST /servers` / `DELETE /servers/{id}`) self-validated (gate) + LIVE-VALIDATED GREEN end-to-end (install + uninstall round-trip; an upstream `kgsm uninstall` interactive-only gap was surfaced + fixed); committed to `main`; frontend gate pending

**Status:** the create/delete write path — the panel's one CREATE op + its delete. Backend **built +
self-validated** at the gate level (the rejection contract, no mutation — exactly M3's discipline) **and
LIVE-VALIDATED GREEN end-to-end 2026-06-19** (real install + uninstall round-trip on `hotrod` — the live run
surfaced + fixed an upstream `kgsm uninstall` interactive-only gap, below); committed to `main` (not pushed);
the frontend gate is deferred with the rest (no SPA).

**No-upstream finding (the discriminating checks, advisor-prompted).** Before building, the whole chain was
traced and confirmed already in place — M8·b is a pure api wiring increment:
- **kgsm-lib:** `IInstanceService.Install(blueprint, installDir, version, name, actor, origin)` /
  `Uninstall(name, actor, origin)` / `GenerateId(blueprint, customName)` all exist, all already provenance-aware.
- **kgsm bash:** `install.sh`/`uninstall.sh` emit `instance-installed`/`instance-uninstalled`, and the
  actor/origin stamping is at the **global** emit chokepoint (`events.sh` reads `KGSM_EVENT_ACTOR`/`_ORIGIN`
  for *every* event), so these two inherit provenance like start/stop — not a per-verb wiring.
- **audit:** `AuditAction.ServerInstall`/`ServerUninstall` + the `KgsmAuditConsumer` handlers for
  `InstanceInstalled/UninstalledData` were already wired (at M5/M6·0) — the echo path is ready, no double-write.
- **The load-bearing assumption, verified in source (not assumed):** `install.sh:127` derives the instance
  name by calling `instances.sh generate-id <bp> --name <id>`, and `_cmd_generate_id` (`instances.sh:961`)
  returns a **valid-unique custom name verbatim** (a duplicate *fails* with `EC_INVALID_INSTANCE`, an unknown
  blueprint *fails* with `EC_BLUEPRINT_NOT_FOUND` — neither silently falls back). So pre-resolving the id via
  the *same* generate-id and passing it as `--name` lands the instance at exactly that id → `job.serverId` ==
  the new instance == the audit row's `serverId` == the verify target. Had install re-generated, the verify
  and the bridge would have silently broken; the source read settled it before any code was built.

**What was built (2026-06-19).** `InstallRequest` DTO (blueprint+name+origin honored; the §3·h form's rest
accepted-but-inert) + `CommandVerb.Install/Uninstall` (NEW `job.Verb`s, deliberately NOT in the
`/commands` `IsKnown` set — dedicated endpoints, not lifecycle verbs); `CommandRunner` `RunInstall`/`RunUninstall`
branches (echo-path — NO direct audit write, the lifecycle case; a shared `Detail()` for kgsm's real failure
text) + `PublishServerRemovedAsync` (uninstall verify: `server.removed` tombstone once gone, else `server.patch`);
`ServersController` `POST /servers` (gate → generate-id assigns the id → `StartInstall` → `202`) + `DELETE
/servers/{id}` (roster `404` → `StartUninstall` → `202`), with a shared `TryResolveOrigin`. The controller
resolves `IInstanceService` optionally → `503` when the engine is unprovisioned (degrade, not a 500).

**Self-validated.** `scripts/smoke.sh` → **47/47** (+3 M8·b: `POST /servers` no-blueprint → `400`,
unknown-blueprint → `400` against **live kgsm**'s generate-id (proving the bash→api reject chain, nothing
created), unknown-server `DELETE` → `404`; + `POST`/`DELETE /servers` added to the no-token `401` sweep) — all
**gate/rejection, no mutation** (a valid `POST` under `AUTH_DISABLED` would *really* install, so smoke only ever
sends the reject cases, exactly like M3). **`tests/Api.Tests` → 135** (+15 `InstallUninstallTests`, the real
pipeline with a switch-on-input `FakeInstanceService` registered via a derived `EngineTestFactory`): the gate
matrix — missing/unknown/bad-origin `400`, unknown-server `404`, operator-gating (`viewer`→`403`,
no-bearer→`401`), engine-unprovisioned `503`, and the `202` + the `install`/`uninstall` `job` shape (verb +
backend-assigned `serverId`) — plus the **no-double-write proof**: a completed install leaves `/audit` empty
(the fake engine emits no event and the runner writes no row, so a stray direct write would surface). Release
**0-warning**. (`AuthTestFactory` was un-sealed so the engine-backed factory can derive from it.)

**Advisor-caught: the model-validation `{error}` envelope (a latent invariant-#4 gap, exposed by M8·b's first
typed body).** `InstallRequest` is the first request body with **typed** fields (`int? port/queryPort/slots`,
`bool? autostart`), so a malformed JSON or a wrong-typed field (`{"port":"abc"}`) trips `[ApiController]`'s
automatic model-state validation **before the action runs** — and that path emitted the framework's
`ValidationProblemDetails`, NOT the frozen `{error}` envelope (`SuppressMapClientErrors` only covers
*result*-based 4xx, not the pre-action filter). **Proven by a failing test first** (the body came back as
`tools.ietf.org/.../rfc9…` ProblemDetails), then fixed with a global `InvalidModelStateResponseFactory` in
`Startup` that emits `{error:{code:"bad_request"…}}` (it also retroactively closes the same latent gap for
M3/M7's bodies — M3's all-optional-string `CommandRequest` just never triggered it). 2 regression tests pin it
(type-mismatch + malformed JSON → envelope, never ProblemDetails). The api `CLAUDE.md` gotcha that flagged this
for "M3/M8" is now updated to "resolved." (The chosen factory over `SuppressModelStateInvalidFilter` keeps an
honest `400` for a genuinely-unparseable body rather than silently binding it to defaults.)

**The verbatim-name chain, closed in source (advisor-prompted, no live host needed).** The verify + audit
correlation rests on `job.ServerId` (the pre-resolved id) matching the `instance_installed` event's instance
name. Beyond `generate-id` echoing a unique name verbatim, `install.sh:153` re-captures `create`'s output —
so `_cmd_create` was read too: it passes `--name` verbatim to `__logic_create_instance`, which uses a provided
name **as-is** (`_instance_name="$identifier"`, `commands/handlers/instances.sh:265`) and *fails* on a collision
(`EC_INVALID_INSTANCE`), never suffixing. So the id flows `generate-id → create → created_instance → the event`
unchanged — the pre-resolve approach is sound end to end.

**Live-validated 2026-06-19 (on `hotrod`, the dev host) — install GREEN; uninstall UPSTREAM-BLOCKED, then FIXED + re-validated GREEN.** Ran the
real round-trip through the API (auth-disabled, bound to kgsm's `kgsm.sock` broadcast path so the audit echo
flowed). **Install: fully proven end-to-end.** `POST /servers {blueprint:"factorio", origin:"api"}` → `202` +
`job{verb:install, serverId:"factorio", state:queued}` → a real install (~3s, version `2.0.76`) → the new
`factorio` appeared on `GET /servers` + `GET /servers/factorio` `200` (honest DTO, `status:stopped`) → a
**`server.install` audit row** off the real kgsm event echo (`success`, `serverId:factorio`, `origin:api`,
`actor:{user,dev,discord}`, `meta:{blueprint:factorio}`) — provenance stamped, **no double-write** (plus an
honest extra `server.update` echo, `0→2.0.76`, since kgsm's install also sets the version). kgsm confirmed the
instance really existed. **Uninstall: blocked by an UPSTREAM kgsm gap (the live run's discovery).** `DELETE
/servers/factorio` → `202` + `job{verb:uninstall}`, but **`kgsm uninstall` is interactive-only** — `commands/uninstall.sh`
has an unconditional `read -rp "Are you sure…(y/N)"` confirmation with **NO `--force`/`-y`/non-interactive
bypass** (the arg parser accepts only `-h|--help`), and on no-confirmation it prints "Uninstall cancelled" and
**returns exit 0**. So kgsm-lib's `Uninstall` (which runs `kgsm uninstall <name>` non-interactively) gets a
*success* code, no removal happens, and no event fires → the API job reports `succeeded` while the instance
survives. **The api code is correct** — it faithfully relays kgsm-lib and can't tell a real uninstall from a
cancelled one because kgsm lies (0 on cancel). The **`server.uninstall` audit MAPPING is proven** anyway: the
CLI cleanup (`echo y | kgsm uninstall factorio`, API still listening) produced a real `instance_uninstalled`
event → a `server.uninstall` row (`warn`, `origin:null` — honestly null for a direct-CLI action, never
fabricated). Host restored to baseline; temp DB/socket removed; the kgsm checkout untouched.

**Upstream fix IMPLEMENTED + re-validated GREEN 2026-06-19 (kgsm + kgsm-lib — NO kgsm-api code change).**
(1) **kgsm bash** (`commands/uninstall.sh`): added a `--force`/`-y`/`--yes` flag that skips the destructive
confirmation; a **declined** interactive prompt now returns the new non-zero **`EC_CANCELLED` (31)** instead of
masquerading as `0` (the very thing that hid this bug). The arg loop no longer `break`s on the positional, so
`--force` is order-independent. The TUI wizard (`__logic_wizard_uninstall_instance`) now passes `--force` — it
already confirms via `__ui_confirm_action`, and its suppressed stdout meant `uninstall.sh`'s second `read` was an
invisible hang (a pre-existing bug, now fixed). `EC_CANCELLED=31` added to `core/errors.sh`. shellcheck clean
(no new findings); unit `test_uninstall_commands.sh` 27/27 (+`--force`/`-y`/order-independence) and integration
`test_install_uninstall_integration.sh` 60/60 (the cancel test updated to expect `EC_CANCELLED`; a new
`--force`-removes-without-prompt test). (2) **kgsm-lib 1.18.0**: `IInstanceService.Uninstall` appends `--force`
to the kgsm args (both provenance + plain paths) — a programmatic uninstall is already confirmed at the calling
surface (the API's operator gate + SPA confirm), never a TTY prompt. Behaviour-only, AOT-safe; unit tests assert
`--force` is passed (incl. the provenance path); the lone EventService socket flake is pre-existing. (3)
**kgsm-api**: bumped the `TheKrystalShip.KGSM.Lib` ref `1.17.0 → 1.18.0` — **no code change** (the api faithfully
relays kgsm-lib). Release 0-warn, tests 135/135. **⚠ Packaging gotcha hit + resolved:** the first
`dotnet pack -c Release` reused a **stale pre-edit dll** (incremental build skipped recompiling), so the 1.18.0
nupkg lacked `--force` and the first re-verify still ran `uninstall <name>` (no flag, exit 31). Fixed by a clean
`rm -rf bin obj` + explicit `dotnet build -c Release` + `pack --no-build`, re-staging the nupkg, and clearing
`~/.nuget/packages/.../1.18.0` before the api restore — verified the `--force` literal is in the consumed dll
(UTF-16 `strings -e l`) before retrying. **Re-validate GREEN:** `POST /servers {factorio}` → install → then
**`DELETE /servers/factorio?origin=api` → the instance was actually removed in ~4s** (`GET /servers/factorio`
`404`, gone from the roster, kgsm confirms), and a **`server.uninstall` audit row landed with `origin:api`**
(`warn`, `actor:dev`) — the provenance-stamped echo the blocked run could not produce (it then wrote nothing).
Host restored to baseline, temp DB/socket removed; the kgsm + kgsm-lib changes are committed (below).

**Honesty note carried to the frontend gate.** `name` is honored as the kgsm **instance name** (kgsm validates
it as an id and falls back to an auto-generated `blueprint-suffix` if it isn't usable/unique) — a true free-text
*display* name is deferred upstream (blueprint metadata curation). The §3·h "name honored" is realized honestly,
not over-claimed. The reserved fields (`dir`/`version`/`port`/`slots`/`password`/`autostart`) are inert, never
half-applied.

**Committed 2026-06-19** to `main` (not pushed) — one commit per repo: kgsm `b00f043` (`--force` + `EC_CANCELLED`),
kgsm-lib `f7b2524` (1.18.0 `Uninstall --force`), kgsm-api `43f8141` (M8·b endpoints + the model-validation
envelope fix + the kgsm-lib ref bump 1.17.0→1.18.0).

### M8·c — 2026-06-19 · config surfaces: `GET /me` built + committed; `/integrations` notification subsystem Increments A (config + real test-send) + B (always-on delivery worker) + C (Slack, the 2nd provider) built & self-validated; `/settings` not started

**Status.** M8's config surfaces. `GET /me` is **built + committed** (`987c52d`). `/integrations` is a
provider-agnostic notification subsystem — **Increment A** (contract + config + a real test-send) is **built +
committed** (`32e410d` + log-leak fix `8f2fbe1`), **Increment B** (the always-on delivery worker — live
notifications fire on real kgsm events) is **built + committed** (`0205ecc`), and **Increment C** (Slack, the
second provider, validating the webhook-family abstraction) is **built + self-validated** (working tree). A
real-webhook live-validate (Discord + Slack) is **owed**. `/settings` is **not started**. M8·c stays `partial`.

**`GET /me` — built (the honest read slice).** A pure projection of the session bearer's claims — no engine,
leaf or DB touch. `MeController` (`[ApiController]`, `[Route("api/v1/me")]`, `[Authorize]`) reads
`SessionClaims.ReadIdentity` + `ReadTier` off `HttpContext.User` and returns `MeResponse { user:
SessionUser{ id, username, display, avatarUrl? }, tier, scopes[] }`. The honest realization (frozen in §6):
- **Read-only — the divergence.** The §3·f surface table lists `/me` as GET+**PATCH** ("Profile: display
  name, handle, **density**"). The editable half needs a per-panel preference store **deliberately not built**
  (architecture.html's statelessness note: prefs that follow a user across devices are out of scope). So PATCH
  + density are deferred, never faked — `/me` surfaces only what the bearer already carries.
- **`[Authorize]`, not viewer-gated.** Any authenticated caller, mirroring `/auth/session` — so a `none`-tier
  caller (verified identity, no role on this host) can read "who am I / why am I 403 elsewhere" instead of being
  shut out of their own identity. No bearer → the `401` envelope.
- **The delta over `/auth/session`.** That endpoint already returns `{user, scopes}`; the one fact `/me` adds is
  the **`tier`** — exactly what the SPA gates its controls on, and the reason `/me` earns its own resource.
- **Validated.** Release **0-warning**; `scripts/smoke.sh` → **48/48** (+1: the `/me` wire shape — camelCase
  `{user,tier,scopes}` — read under the auth-disabled synthetic admin (`tier:admin`, `user.id:discord:dev`); plus
  `/me` folded into the auth-enabled no-bearer 401 sweep). `tests/Api.Tests` → **143** (+8 `MeTests`: the identity
  snapshot + scopes projection, `tier` reflected verbatim across viewer/operator/admin, the **none-tier `200`**
  reachability (the who-am-I behaviour that distinguishes `/me` from the viewer-gated reads), and the
  refresh-as-access / wrong-signature `401`s through the real pipeline). Mirrors `/auth/session` + `TierMatrixTests`.

**`/settings` — not started (scope its honest backing first).** §3·d is "Assistant endpoint & general
preferences." The **assistant endpoint URL** is a real, persistable config value; the "general preferences" hit
the **same missing-preference-store wall** as `/me`'s PATCH half. The honest move is to scope the persistable
subset (and decide whether it needs the first non-audit EF entity) before building — not to ship a settings
surface backed by a store that does not exist.

**`/integrations/discord` — a notification subsystem (the discriminating finding).** §3·e is NOT an endpoint
wire: the M4 `ApiOptions.Discord*` is **auth role-resolution only** (client id/secret, bot token, guild, role→tier
maps — used only to resolve a login's tier via `GET /guilds/{guild}/members/{user}`); §3·e asks for a
**notification-routing integration** (a stored webhook secret masked-on-read, an `events[]` routing config over a
server-defined catalog, a real `POST /test`). A grep of `src/` confirmed **zero backing** (only EF entity was
`AuditEntry`). An earlier "what's next" note wrongly called it buildable-now by conflating the two — corrected.

**Two decisions (the user's call, 2026-06-19).** (1) **kgsm-api owns it**, wired **behind a provider abstraction**
so Slack/Telegram follow (§3·e's `/integrations/{provider}`). (2) **COEXIST with `kgsm-bot`** — kgsm-bot already
posts online/offline/uninstalled to *per-instance channels* via the bot gateway (a second consumer of the same
kgsm event stream), so the API posts to its **own configured webhook** and kgsm-bot is unchanged (parallel
surfaces; no double-post unless aimed at one channel). **Scope guard:** one-way webhook delivery only — the §3·e
two-way control **bot** + slash-commands stay kgsm-bot's, so the Discord view's `bot` is honestly `null`.

**Increment A — built + self-validated (contract + config + a real test-send; NO delivery worker).** New:
`IntegrationEntity` (the first non-`AuditEntry` table, in the same `AppDbContext` so the existing `EnsureCreated`
creates it — the dev DB must be deleted once) + `IntegrationStore` (the `AuditService` scope-per-op + write-gate
pattern, JSON columns for `events`/`settings`); the **thin** `INotificationProvider` seam + `NotificationCatalog`
(only deliverable events) + `DiscordNotificationProvider` (webhook POST via a typed `HttpClient`, the
`DiscordIdentityResolver` pattern); `IntegrationDto`; `IntegrationsController` (**admin**-gated GET list / GET
`{provider}` / sparse PATCH / POST `{provider}/test`). DI: `IntegrationStore` singleton +
`AddHttpClient<INotificationProvider, DiscordNotificationProvider>`. **Honest realization (frozen §6):** `bot:null`
(one-way only); catalog `online·offline·crash·update·installed·backup` (`resource`/`join` omitted — no source);
webhook **masked on read** (hint), write-only on PATCH, plaintext at rest in the host-local SQLite (consistent with
the env-stored bot token); `cadence` accepted (`every|once|digest`) but enforcement is incremental (`every`=B,
`once`/`digest`=C — accepted-but-inert, the M8·b reserved-field pattern); `/test` honest (`409` unconfigured · `502`
delivery-failed · `202` ok — never a faked ok).

**Advisor-caught: the webhook secret leaked into the LOG channel (now fixed).** The masking covered the wire
(hint-only, write-only, body-asserting tests) but the webhook URL *is* the secret (`.../webhooks/{id}/{token}`),
and the provider POSTs via the `IHttpClientFactory` client whose default logging handler logs `Start processing
HTTP request POST {uri}` at **Information** — so every send wrote the token to the app log, a channel the body
tests can't see. Fixed by `.RemoveAllLoggers()` on that typed client (Startup). **Proven by a failing-then-passing
log-capture test** (boots the real production pipeline, stubs only the outbound HTTP, captures Information logs,
asserts the token never appears — drop the fix and it fails on the captured `POST https://discord.com/…/{token}`).

**Validated:** Release **0-warning**; `scripts/smoke.sh` → **52/52**
(+4: the list + the `bot:null`/honest-catalog shape + the unconfigured-`409` + the masked-secret PATCH round-trip;
plus `/integrations*` in the no-bearer 401 sweep — admin gate proven in tests, smoke runs the synthetic admin).
`tests/Api.Tests` → **166** (+23: 11 `DiscordProviderTests` — mask/validate/test-send with a faked
`HttpMessageHandler`, the honest catalog; 12 `IntegrationsApiTests` — the admin gate (viewer/operator `403`,
no-token `401`), unknown-provider `404`, the masked-secret PATCH→GET round-trip never echoing the raw URL, the
PATCH validation `400`s, the `/test` `409`/`202`, and the no-token-in-logs guard).

**Increment B — the delivery worker — built + self-validated (live webhook still owed).** `AuditService.AppendAsync`
publishes every audit row to a singleton **`INotificationBus`** (a bounded `Channel`, `DropOldest` + logged-on-drop):
the **always-on** tap — the audit write is unconditional, while the `StreamHub` pumps are subscriber-gated, so a
notification fires whether or not anyone is watching; and because the audit log is the single, no-double-write
writer, one action → exactly one notification. The bus keeps only catalog-mapped actions (`NotificationCatalog.
CatalogIdForAction` — `server.start`/`server.restart`→`online`, `stop`→`offline`, `crash`→`crash`, `update`,
`install`, `backup.create`; everything else dropped before the queue). A **`NotificationDeliveryWorker`** (always-on
hosted service, the audit-consumer/alert-engine shape) drains it, **scope-per-event** (the typed-client providers are
disposable), and for each provider gates on **enabled + secret + rule.enabled + cadence==`every`**, then
`provider.SendAsync`. `INotificationProvider` gained `SendAsync`; `DiscordNotificationProvider` formats per action
(🟢/🔄/⚪/🔴/⬆️/📦/💾) with an optional ops-role ping (`Settings["pingRoleId"]` + `allowed_mentions` scoped to that one
role, which also kills an accidental `@everyone`); a shared `PostAsync` backs both `Test` and `Send`. **Three
advisor-vetted realizations:** (a) **`server.restart`→`online`** (a completed restart is back up) so the watchdog's
autonomous crash-restart delivers the "back online" that pairs with its crash, not a silent gap; (b) a
**per-`(provider,server,catalog)` 60s anti-spam window** — the first crash fires, repeats within the window are
skipped + logged, so a crash-loop can't self-DoS the webhook (Discord rate-limits ~30/min) exactly when a server is
dying; a mass reboot (N servers → N distinct keys) is NOT suppressed (bounded by host server count, accepted; heavier
shaping is C); (c) **`once`/`digest` deliver ZERO in B**, logged at Information ("set 'every' to receive it") — not a
silent black hole. **Validated:** Release **0-warning**; `tests/Api.Tests` → **190** (+24: `NotificationMappingTests`
the action→catalog map; `DiscordSendTests` the `SendAsync` format/ping/honest-failure with a recording handler;
`NotificationDeliveryE2ETests` — an audit row appended through the real `AuditService` reaches a recording webhook,
with the rule-disabled / once-cadence / suppression gates proven **deterministically** via a barrier event + a
`SemaphoreSlim` arrival latch, no sleeps); smoke unchanged **52/52** (B adds no HTTP endpoint — a background worker,
proven by the e2e tests). **Owed:** a real-webhook live-validate (smoke must never post to real Discord).

**Increment C — Slack, the second provider — built + self-validated (the user's call: Slack over Telegram;
once/digest cadence deferred; resource/join still out — no honest source).** This is the honest validation of the
abstraction you asked for. Extracted an abstract **`WebhookNotificationProvider`** base holding the genuinely-shared
logic — the honest `PostAsync` (Discord returns `204`, Slack `200`; both `IsSuccessStatusCode`), the `Test`/`Send`
orchestration, the catalog⋈rules `EventViews` overlay, and a `MaskWebhook(url, marker)` hint that **reproduces
Discord's exact committed hint** (`marker:"webhooks"` → `…/webhooks/{id}/{tok}***`). Refactored
`DiscordNotificationProvider` onto it (behavior identical — its 23 unit + e2e tests stayed green through the move),
then added **`SlackNotificationProvider`** (`hooks.slack.com` exact-host + `/services/` validation — not `EndsWith`,
which would accept `notslack.com`; mrkdwn `*bold*`; `{text}` payload; optional `Settings["pingSubteamId"]` →
`<!subteam^id>`; **`&<>` escaped** in dynamic text — the Slack analog of Discord's allowed_mentions care) +
`SlackIntegrationView` (**no `bot` block** — Slack incoming webhooks have no Discord-style control bot; omitting it is
honest, a fabricated null would not be). Registered identically
(`AddHttpClient<INotificationProvider, SlackNotificationProvider>().RemoveAllLoggers()`); the worker, bus, catalog,
and controller were **already provider-agnostic**, so Slack is picked up with **zero** change there — the abstraction
held. **Honest framing (advisor):** the base validates the **webhook-secret-URL family** (Discord + Slack); a provider
whose secret is NOT the URL — **Telegram** (a bot token, a fixed `api.telegram.org/bot<token>/sendMessage` endpoint,
a `chat_id` from settings) — would implement `INotificationProvider` directly, so it is the next real test of the
interface, not of this base. **Validated:** Release **0-warning**; `tests/Api.Tests` → **207** (+17: `SlackProviderTests`
— mask/validate/format/escape/subteam-ping/honest-failure with a recording handler; `SlackApiTests` — the list now
shows **both** providers (the abstraction wired), the no-`bot` view, the admin gate, the masked PATCH round-trip);
smoke → **54/54** (+2: `/integrations/slack` shape + masked round-trip; the list asserts both providers). **Owed:** a
real Slack-webhook live-validate. **Increment C.2** (the `once`/`digest` cadences — they need real semantics
decisions) stays deferred; `resource`/`join` stay out (no honest source).

**Committed.** `GET /me` + the M8·b header sync (`987c52d`), `/integrations` **Increment A** (`32e410d` + the
log-leak fix `8f2fbe1`), and **Increment B** (`0205ecc`) are committed on `main` (not pushed). **Increment C**
working-tree changes await this milestone's commit (on user request).

### File browser (Tier 3 #12) — 2026-06-24 · `GET/PUT /servers/{id}/files…` built + self-validated + LIVE-VALIDATED on both real servers; frontend WIRED

The instance file browser + editor behind kgsm-web's `FileBrowser` (`docs/file-browser-plan.md`). **API-side
only — no upstream change** (no kgsm/kgsm-lib/monitor/watchdog), no EF migration (audit reuses `AuditEntry`).
The contract is in §6 ("File browser"). New code: `Services/Files/InstanceFileService.cs` (the jailed I/O core
+ the typed results), `Services/Files/PosixFile.cs` (the `lstat` P/Invoke — the only honest socket/FIFO oracle),
`Controllers/ServerFilesController.cs` (the three endpoints, operator-gated, instance-resolve/degrade like
config/backups), `Contracts/FilesDto.cs`, `AuditAction.FileWrite`, the `FilesMaxEntries`/`FilesMaxEditBytes`
`ApiOptions` knobs + appsettings, and the Startup registration.

**Security proven empirically, not by string-logic** (the advisor's load-bearing point): a temp-dir probe
confirmed (a) the `lstat` offset-24 `st_mode` read classifies regular/dir/symlink/socket/FIFO correctly on the
Arch x86-64 target, and (b) an **intermediate-directory symlink** (`working_dir/foo`→`/etc`, request
`foo/passwd`) sails through a lexical prefix check — so the resolver walks **every** component (POSIX realpath).
The `InstanceFileServiceTests` suite then re-proves it against real on-disk symlinks (leaf + intermediate),
a real unix socket, binary/too-large, dirs-first truncation, and the atomic save (etag/412/refuse-create/
refuse-binary). The `FileBrowserApiTests` suite proves the operator gate (viewer `403`, no-token `401` on read
AND write), the degrade codes, and the **secret-hygiene regression** — the `file.write` audit row carries
path/size/sha256 but the raw `/audit` response never contains the saved content.

**Validation:** unit + HTTP tests **345/345** (+35); `scripts/smoke.sh` **65/65** (+7 file checks + the files
endpoints folded into the no-token sweep), 0-warn Release. **LIVE-VALIDATED 2026-06-24** against the running api
pointed at the dev kgsm checkout (the two real instances `factorio-test` + `terraria-hardmode`): listed the real
working-dir tree (`install/saves/logs` children, the `.config.ini`/`.manage.sh` files, dirs-first), read a real
config with an sha256 etag, refused `saves/default.zip` as `file_binary`, refused a `../../../../etc/passwd`
traversal with `404`, saved a real text file (atomic write confirmed on disk) and got a fresh etag, got `412` on
a stale etag, `404` on a non-existent save, and confirmed the `file.write` audit row (origin `ui`, host `hotrod`)
with path/size/sha256 and **no content**. **Frontend WIRED** (kgsm-web): the `api.put` seam added to `apiClient.js`,
`FileBrowser.jsx` rewritten to a lazy tree + raw-textarea editor (etag Save / Reset / 412-reload / truncation
banner / binary-or-symlink shown-not-openable), `App.jsx` passes `server`, `WIRING.md` moved to **DONE**. The
kgsm-web `smoke-live.mjs` gained a Files phase (page renders the real tree + a get/put round-trip + 412 + jail) —
7 green, zero regressions (baseline confirmed by stashing the change).

**Owed-to-human:** clicking through the editor in a real browser (the jsdom smoke proves the render + the
put-seam round-trip, not the button DOM events); and deploying the new api binary to the host so the deployed
SPA (`:8080`) sees it. **Uncommitted** pending the user's commit request.
