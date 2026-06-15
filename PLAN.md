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
- ~~**Bearer mechanism** (EF `sessions` table vs JWT)~~ — **RESOLVED at M4·a: stateless JWT**
  (HMAC; access ~15 min + refresh with an 8h cap). No session table, no user row — honors the §3·f
  "no user row anywhere" doctrine and keeps M5 as the first EF migration. Trade: no instant
  server-side revocation, bounded by the short access TTL.
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
| `Server` DTO (honest realization) | **M1·b** | `architecture.html §3` — **frozen 2026-06-14.** `{ id, name, blueprint, status, version?, runtime, hostId, metrics? }`; `metrics:{ cpuPctCore, memBytes, ioReadBps?, ioWriteBps?, pids }` or **`null`**. Stable keys, explicit nulls. **Divergences from the §3 example (the negotiated honest-vs-aspirational contract):** `status` is `running\|stopped\|unknown` (from `Reading<InstanceRuntimeStatus>`), NOT `online\|offline\|updating\|crashed\|installing` (transitional states need the M3 job tracker + crash detection); `metrics.cpuPctCore` is **% of one core (can exceed 100)**, not `cpu` 0–100; `memBytes` replaces `ram{used,max}` (no honest memory limit); **omitted as unsourceable**: `players`, `ip`, `ram.max`, `updatedAt` (no state-change tracking until M2), and the curated `game` display name (we emit the real `blueprint` id — metadata curation deferred, never guessed). Join key: instance id (`monitor ServerMetrics.Id` == kgsm instance name == lib dict key). `/servers/{id}` == the list element shape (full detail later). |
| `Host` DTO + capacity (`cpuPct`/`mem`/`disks`) | M1·a | `architecture.html §4·a` — **frozen 2026-06-14.** `{ id, label, status:"online", cpuPct, mem{used,total} GiB, disks[]{mount,used,total} GiB, capabilities }`. **Divergence (record for the gate):** capacity (`cpuPct`/`mem`/`disks`) is **nullable** — `null` when metrics ≠ operational (the §4·a example always shows numbers). `/hosts/{id}` currently == the list shape; §244's sensors/network/processes are deferred. |
| Capability record `{ provisioned, status, since?, message?, info? }` | M1·a · **refined M2** | `architecture.html §4·b` — **frozen 2026-06-14, refined 2026-06-15.** **Two independent axes, never conflated:** `provisioned` (bool) is the **fixed** "what leaves this host has" — resolved once from config, the one-time set the frontend negotiates at connect; it **never flips at runtime**. `status` ∈ `operational\|degraded\|down\|unknown` is the **live availability**, driven by frequently polling each leaf's health (M2 `LeafHealthMonitor`, ~2s) — monitor/assistant `GET /health`, watchdog `IsReadyAsync` via kgsm-lib. A leaf failing flips `status` (operational→down→operational), **never** `provisioned`: the capability is "temporarily unavailable, still there", never "lost" — `provisioned:true, status:down` IS the down notification (we never invent a softer status nor suppress the flip). `provisioned:false` → client-derived `absent`. `since` **now emitted** (M2): the timestamp this api *observed* the flip (not an authoritative leaf-change time). `degraded` reserved (a restarting leaf is `down`); cold (pre-first-poll) reads as `unknown`. **Divergences:** `info.intervalMs` (camelCase, ms) replaces the example's `info.interval_s`; `transport` **omitted** (REST + WS, not `"sse"`). |
| Monitor `/metrics` wire shape (`Snapshot` graph) | M1·a | **shared package** `TheKrystalShip.KGSM.Monitor.Contracts` — the DTO graph + source-gen camelCase JSON, built in kgsm-monitor and consumed here so the contract is solid at build time. **Drift rule:** any contract change MUST bump the package `Version` and the api's `<PackageReference>` (a same-version repack is silently served stale from the NuGet cache). |
| WS stream envelope + topic/type vocabulary | **M2** | `architecture.html §3·b/§3·j` — **frozen 2026-06-15.** Endpoint `GET /api/v1/stream` (WebSocket; **unauthenticated until M4** — a pre-auth *read* surface, less severe than M3's mutation but flagged). **Inbound:** `{ type: "subscribe"\|"unsubscribe", topics: [...] }`; unknown command type ignored; unknown/future topics accepted silently (forward-compat for `jobs` M3 / `audit` M5 / `alerts` / `console`); **no ack or error-frame protocol yet.** **Outbound:** `{ topic, type, data }`, patch-only (the client `hydrate(REST) + applyPatch(WS)`; **no snapshot on subscribe** — §3·j re-hydrates via REST on (re)connect). **Topics (M1-backable subset):** `servers` · `servers/{id}/metrics` · `hosts/{id}/metrics` · `hosts/{id}/capabilities`. **Message types** (only `server.patch` is doc-given; the rest are ours, negotiated like the M1·b DTO — all centralized in `Realtime/StreamProtocol.cs`, never inline strings): `server.patch` (data = the **frozen M1·b `Server`**, NOT the §3·b example's `{status:"online", players}`; carries the full element incl. a **point-in-time `metrics` block that may lag** the dedicated `servers/{id}/metrics` tick — merge by id), `server.removed` (`{ id }` tombstone), `metrics.tick` (`ServerMetricsDto`), `host.metrics` (`HostMetricsDto` = the capacity portion of the `Host` view; `net`/`temp` omitted, never fabricated), `capabilities.patch` (`HostCapabilities`). **`servers` carries status/roster only — NOT the 1s metric firehose** (resource ticks live on `servers/{id}/metrics`; a deliberate divergence from §3·b's "resource deltas" wording, smoke-proven the `servers` topic stays quiet under ticking metrics). **Honesty:** monitor-down → metric topics go **silent** (never a replayed stale frame) and `hosts/{id}/capabilities` flips metrics `down` — that flip is what explains the silence. **Capability availability is driven by the always-on `LeafHealthMonitor`** (frequent `/health` polls, the single source feeding both this stream and the REST `GET /hosts`), which stamps `since` on each flip — see the Capability-record row. The `capabilities.patch` keeps `provisioned:true` through a down→up cycle: degrade **and** recover gracefully, capability never "lost". |
| Command verbs + `job` shape + `command.verified` | **M3** | `architecture.html §5·d` — **frozen 2026-06-15.** **Endpoint** `POST /servers/{id}/commands` body `{ verb }` → `202` + `{ job }`; closed, server-defined verb set **`start`·`stop`·`restart`** (**`update` deferred** from the first cut — long-running + version-changing, settles on a version re-check not a run-state one). **Errors:** unknown/missing verb → `400 bad_request`; unknown server → `404 not_found`; an obvious no-op against the real status (start-when-running / stop-when-stopped) or a command already in flight for that server → `409 conflict`. **`job`** = `{ id, serverId, verb, state, createdAt, settledAt?, error? }` (opaque `job_…` id; ISO-8601 UTC `Z` times; `error` set only on `failed`, the engine's real detail — never a fabricated success). **Divergence (honest-vs-aspirational, the same negotiated call as the M1·b DTO):** `job.state` is the **job's own** lifecycle `queued→running→succeeded\|failed`, NOT the §5·d example's server-shaped `state:"running"` — the affected server's authoritative/optimistic status rides the `servers` topic via `server.patch`, and the client derives the optimistic display from the verb (the same topic-separation discipline as the metric topics). **WS:** the `jobs` topic carries a single **`job.patch`** (the full `job` on every transition, coalesced by job id — patch-only, exactly like `server.patch`). **Gate (state guards):** minimal/honest — only the obvious no-ops; the engine (kgsm→watchdog/Docker) owns everything subtler, surfacing an impossible transition as the job's `failed` + its error (the API never fabricates admissibility kgsm does not enforce; `unknown` status never blocks). **Verify** (the §5·d `command.verified` for the direct write path): on settle, a fresh run-state read → an explicit `server.patch`. **Permissions** gate at M4; jobs are **in-memory** (SQLite + audit at M5). The propose/confirm half of §5·d (`command.proposed` over SSE) is the assistant flow (M7), not M3. |
| Auth session + tiers + 401/403/login_required | **M4·a** | `architecture.html §3·f` — **frozen 2026-06-15 (M4·a).** **Bearer = stateless JWT** (HMAC; access ~15 min + refresh 8h cap; no session table). Endpoints `/auth/discord/start` (302→authorize), `/auth/discord/callback` (`{ verdict:"ok"\|"denied", tier, token, refresh?, userId }`), `/auth/session/refresh` (refresh bearer → `{ token }`), `/auth/session` (`{ user:{ id, username, display, avatarUrl? }, scopes }` or `401`), `/auth/logout` (`204`). Tiers `admin·operator·viewer·none` resolved from the guild role via the **bot token** (`GET /guilds/{guild}/members/{user}` — the only path to roles; the `identify guilds` user scopes don't carry them). `401` (no/invalid/expired bearer) recoverable; `403` (`none`/insufficient tier) terminal. **Divergences (the negotiated honest-vs-aspirational call, like the M1·b DTO / M3 job.state):** (1) camelCase `userId` (not the §3·f example's snake_case `user_id`) — one casing across the surface; (2) `GET /auth/session` returns the **login-time profile snapshot** embedded in the token, NOT a fresh live Discord fetch — the §3·f "fetched live" can't hold once the Discord token is discarded (which §3·f also requires), so snapshot is the honest realization; (3) role re-check happens only at a full bounce (≤ the 8h cap), not on refresh (refresh skips Discord); (4) `/auth/session/refresh` takes the refresh token in the `Authorization: Bearer` header (the `{host}` body is accepted but not required) — a per-host-API simplification of the §3·f `{host}`-body shape. **WS:** the `/stream` bearer rides `?access_token=` (a handshake can't set a header). **Tier gating:** viewer = reads + stream, operator = + the command `POST`, admin = diagnostics (`_throw`/`_dbcheck`) + reserved (settings/install/audit-config, M5/M8). **Secure-by-default:** an authorization `FallbackPolicy` requires an authenticated caller on any endpoint without explicit `[Authorize]`/`[AllowAnonymous]`; only `/health` + `/api/v1` opt out (the SPA's pre-login reachability probes). **CSRF (added M4·b):** `/auth/discord/start` sets a one-time HttpOnly `state` cookie (stateless double-submit; `SameSite=Lax`, `Secure` only under https); `/auth/discord/callback` returns `400 invalid_state` on a missing/mismatched state before any Discord exchange. **M4·b — LIVE-VALIDATED 2026-06-15:** the real Discord exchange + bot-token role lookup resolved an admin login end-to-end (§8); login endpoints `503` only until the Discord app/bot-token/guild/role-map are configured. |
| Audit record + closed `action` vocabulary + SQLite schema | **M5** | `architecture.html §3·d` — **frozen 2026-06-15.** **Endpoint** `GET /api/v1/audit?cursor=&limit=50&severity=&serverId=&actor=` → `{ data, nextCursor }`, **newest first**, keyset on the opaque `rowid` cursor (`RowId < cursor` ordered `DESC`; `nextCursor` = the last row's rowid, or null when the page is short). Filters map 1:1 to indexed columns; `limit` clamped (default 50, max 200). **Record** = `{ id (evt_…), ts (Z), origin, actor:{ kind:user\|system\|token, name, provider:discord\|system\|api? }, action, severity:info\|success\|warn\|danger, target:{ kind,id,name }?, serverId?, hostId?, summary, meta? }`. **WS:** the `audit` topic carries **`audit.append`** (one full record; the client **prepends** — events are immutable). Unlike the metric/status patches it is **NOT supersede-by-latest**: the coalesce key is the unique event id, so distinct appends never collapse. **Action vocab wired in M5** (the honestly-sourceable subset): `server.start\|stop\|restart\|update\|install\|uninstall`, `backup.create\|restore` (from kgsm events), `auth.login\|logout` (API-internal). **Deferred (no source yet):** `server.crash` (watchdog, M6), `config.change`/`network.ports.open`/`player.*`/`host.*`/`discord.*`/`settings.change`. **Source model (the no-double-write decision):** kgsm **owns** `server.*`/`backup.*`, so the API records the engine's **event echo** — it never writes an audit row when it issues a command; instead the command path **stamps** `actor`(bearer identity)+`origin`(declared surface) which ride the event and are read back off it. `auth.*` has no kgsm event → written directly (no double-write). **Divergences (the negotiated honest-vs-aspirational call, like the M1·b DTO):** (1) **`origin` is nullable** (the §3·d DDL says `NOT NULL`) — a direct-CLI engine action has no product surface, so the engine emits `null` and we persist that, never fabricate a surface; (2) the example's **`meta.jobId` is not populatable** — no correlation id round-trips the stateless engine, so `meta` holds action-specific detail (e.g. `{oldVersion,newVersion}`, `{blueprint}`, `{source,version}`, the login `{tier}`) instead; (3) the command path's `origin` is **caller-declared** (`ui\|assistant\|discord\|api`, default `api` — literally true; `system` reserved for autonomous engine actions and rejected), **never derived from the actor** (the two axes stay independent). **Honest boundary:** events emitted while the API isn't listening are **never audited** (stateless engine, no backfill) — inherent to a downstream-consumer design. **Storage:** the §3·d SQLite schema, created via **`EnsureCreated`, NOT an EF migration** (greenfield/dev authority — wipe the DB on a schema change). Gated at **viewer** (a core read surface). |
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

### M5 — 2026-06-15 · audit log (the append-only event-persistence consumer) self-validated; frontend gate PENDING · live socket round-trip OWED

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
