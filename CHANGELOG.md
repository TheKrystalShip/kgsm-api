# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Changed (v0.26.0) — metrics history is proxied to kgsm-monitor
- **kgsm-monitor is the single source of truth for metrics history.** The API no longer persists
  metrics: `GET /servers/{id}/metrics/history` and `GET /hosts/{id}/metrics/history` keep their routes,
  viewer gate, and existence checks (unknown id → 404), then **relay the monitor's `GET /metrics/history`
  body verbatim**. Monitor absent/unreachable → an honest empty response (200), the same graceful
  degrade the SPA already handles. The live path (`MonitorClient.GetLatestAsync` → `MetricsPump`) and
  the `Snapshot` contract are unchanged.
- Removed the API-side persistence subsystem: `MetricsSampler`, `MetricsMaintenanceService`,
  `MetricsHistoryStore`, `MetricsDbContext` (+ `metrics.db`), and the `KGSM_API_METRICS_HISTORY_*` /
  `KGSM_API_METRICS_PERSIST_MS` / retention config keys. `MonitorClient` gains `IMonitorHistoryClient`
  (the history read seam the proxy controller uses). Existing `metrics.db` history is discarded on
  cutover — the monitor's windows refill on its own cadence.

### Added (v0.25.0) — cluster resource visibility (P2)
- **`GET /api/v1/peers/self/{resources|capabilities|library}`** — what a node exposes to a cluster peer
  (**cluster-token authed + disable-gated**, the same fail-closed preamble as `/peers/inbox`; unlike
  `/peers/identity` a resource read IS gated, so an explicitly-disabled peer gets `403 peer_disabled`).
  `self/resources` is a lean projection of the §4·a host capacity strip
  (`{ id, label, status, cpuPct, mem, disks }`) — capacity is honest `null` when no metrics snapshot exists,
  never a fabricated figure; `self/capabilities` returns the §4·b capability block verbatim;
  `self/library` returns the installable-game catalog verbatim (empty when the engine is unprovisioned).
- **`GET /api/v1/peers/{id}/{resources|capabilities|library}`** — the **server-side node-proxy** relay
  (**admin-gated**): mints a cluster service token and fans the read out to the peer's `self/*` surface,
  returning the peer's body verbatim (reuse the existing DTOs). This is the one node-proxied path — consumed
  by the on-demand "find a node with capacity" logic and (later) the assistant, **never by the SPA** (which
  reads a peer's resources directly over its own native session; §8). A down peer degrades to
  `502 peer_unreachable`, an unknown id to `404`, a disabled peer to `403 peer_disabled` — never a 500.
- New `ClusterPeerRelay` service (reuses the `OutboxDrainer` named `HttpClient` — mint-authed, 10s-bounded —
  so every node-to-node call shares one client) + `ClusterResourcesView` DTO. Self-validated by
  `ClusterResourceRelayTests` (9 facts: self/* auth + honest-null capacity, relay 404/403/502/viewer-gate,
  and a two-node happy path routing A's relay into B's real pipeline). 746/746 tests.

### Added (v0.24.0) — cluster SPA-facing endpoints (G1 viewer roster, G2 vouch initiator)
- **`GET /api/v1/peers/roster`** — the **viewer-gated** node list the browser reads to auto-populate its
  registry ("add one, see all"). The admin `GET /peers` leaks management detail (gossip URL, `enabled`,
  `apiVersion`) and is admin-only; this is the lean, tier-scoped projection any authenticated user gets:
  `{ nodes: [{ nodeId, label, clientUrl, membership, status, latencyMs }] }`. Enabled peers only (disable is
  an admin management state a viewer never sees or is handed a URL for); every membership state is otherwise
  present and honestly labelled (`GossipState.Display` yields the derived `joining` for un-authenticated
  hearsay). `clientUrl` is the **advertised** browser-reachable URL (`PeerEntity.Url`), never the node-to-node
  gossip URL; `label` = `Nickname ?? NodeId`.
- **`POST /auth/cluster-session/request`** — the **user-authed** initiator to the node-to-node vouch receiver
  (`POST /auth/cluster-session`). The browser holds no cluster secret, so it cannot call the receiver directly;
  it calls this on a node it **is** logged into (A), which reads the caller's asserted identity **from A's own
  session claims** (`SessionClaims.ReadIdentity`/`ReadTier` — **never** the request body, which carries only
  `nodeId`, so the tier can't be laundered), mints a cluster service token, relays to the target peer's receiver
  (`GossipUrl ?? Url`), and returns B's `201 { accessToken, refreshToken, sid, expiresAt }` **verbatim**. Any tier
  (viewer floor — SSO preserves the caller's tier). `400 bad_request` (missing nodeId) / `401 unauthorized` /
  `404 unknown_node` / `403 peer_disabled` / `502 peer_unreachable` (fail-closed — a relay failure never
  fabricates a session). This is the server-side half of the SPA's lazy vouch-on-`401`.
- **Tests:** +11 (`ClusterSsoTests`, **737/737**) — the viewer roster shape/enabled-filter/`clientUrl`-not-gossip/
  `joining`-derivation/`401`; the two-node vouch-initiator happy path (A→B, B's token authenticates on B), the
  claim-sourced-tier security proof (viewer caller → viewer session on B, operator → operator), and the
  `401`/`400`/`404`/`403`/`502` matrix.

### Added (v0.23.0) — cluster single sign-on (SSO, P1)
- **`POST /auth/cluster-session`** — the SSO vouch endpoint (`PLAN-peers.md` P1). Cluster-token authed +
  disable-list gated (the same fail-closed preamble as `/peers/inbox`): a peer node presents a valid cluster
  service token and asserts an already-authenticated identity `{ discordId, username, displayName, tier }`;
  this node mints its **own native session** (own `sid` in its own registry, own sliding refresh) and returns
  `{ accessToken, refreshToken, sid, expiresAt }`. It never calls Discord — the vouch *is* the trust (§0 shared
  guild + shared secret). An unparseable/empty tier floors to `viewer` (authenticated, never escalated, never
  denied outright). `401 invalid_cluster_token` / `403 peer_disabled` / `400 bad_request`. The mint is audited
  as `auth.cluster_session` (actor = the vouched user, `origin: api`, the vouching node id in `meta.peerNode`).
- **Cluster-wide logout** — the self `POST /auth/session/revoke {all:true}` and the admin
  `POST /auth/users/{userId}/sessions/revoke-all` now, after the local revoke, enqueue a durable
  `session.revoke` (`{ scope: "user", discordId }`) to peers over the message bus (the `session.revoke` inbox
  handler already existed). Durable to down nodes — a peer that is offline at logout time is revoked on its
  return via outbox redelivery. Peers-only (the local effect already ran in-process); a single-`sid` self-revoke
  stays node-local. Inert when `!ClusterEnabled`.
- **Durable fan-out targets first-hand-`alive` peers only** (`RosterClusterTargetProvider`, the locked P1 rule):
  a durable, identity-carrying message fans out only to peers with `MembershipState == alive` **and** a stamped
  `LastSeen` (first-hand-authenticated) — a purely gossip-learned hearsay peer is *stored* `alive` with a null
  `LastSeen` (it displays as `joining`) and is **excluded**, so a gossip-injected phantom URL can never receive a
  secret-bearing message nor sit in the outbox retrying to a corpse. Ephemeral gossip is unaffected (it reads
  `ListEnabledAsync` directly, not this provider).
- **Self-validated** (§9 P1, +14 tests, **726/726 green**): a two-node in-process vouch (201 + a real
  `SessionEntry` on the receiver + the returned token authenticates a follow-up call); vouch auth/validation
  failures (401 missing/garbage/wrong-secret, 403 disabled peer, 400 missing id, tier floors to viewer);
  cross-node logout-everywhere (local revoke + bus delivery revokes the peer's session); the durable→alive filter
  (a hearsay/phantom peer receives **no** outbox row, plus focused `RosterClusterTargetProvider` unit facts); and
  down-node redelivery (queued while down → delivered on return).

### Added (v0.22.0) — cluster membership gossip (convergence, P0.5)
- **Masterless anti-entropy gossip** (`PLAN-peers.md §2·b`, P0.5) turns the manually-seeded mesh into
  "add one, join all" — no new service or dependency. `GossipWorker` (a `BackgroundService`, inert unless
  `ClusterEnabled`) each `KGSM_API_CLUSTER_GOSSIP_MS` round advances the failure timers, picks one random
  enabled non-terminal peer, and runs a push-pull roster exchange with it.
- **`POST /api/v1/peers/sync`** — the ephemeral roster-exchange endpoint (cluster-token authed +
  disable-list gated, same fail-closed posture as `/inbox`). Deliberately separate from the durable message
  bus: fire-and-forget, **no `cluster_outbox` row**, no retry (G4). The caller pushes its roster; this node
  merges it and returns its own for the caller to merge back.
- **`RosterMerger`** — the pure, unit-tested merge core (G2): a strictly higher incarnation always wins
  (refutation), equal incarnation breaks by SWIM state precedence, **fresh first-hand evidence outranks
  equal-incarnation hearsay**, a locally-disabled row is never resurrected by gossip, and a stale/negative
  report about ourselves raises `SelfIncarnation` to refute it.
- **Two liveness axes on `PeerEntity`, never conflated:** `Status` (this node's first-hand probe,
  poller-owned) and the new `MembershipState` (`alive`/`suspect`/`dead`/`left`, gossip-converged, ordered
  by `Incarnation`). A gossip-learned peer is hearsay-provisional — surfaced as the derived `joining` on
  `GET /peers` until this node authenticates it first-hand (the poller now pulls `/identity`, checks the
  `cluster` cap + `apiVersion`, then promotes it to `alive`, G3). New `StateChangedAt` column drives the
  failure timers; an idempotent `ALTER TABLE peers ADD COLUMN` lands both on an existing P0-shape DB
  without a wipe.
- **Failure detection = a last-evidence clock** (`GossipService.AdvanceFailureTimersAsync`): evidence is
  mutual — our own successful probe OR an authenticated inbound sync **from** the peer (`RecordInboundContactAsync`,
  stamped against the token's node id, never the spoofable body `From`). No evidence for
  `KGSM_API_CLUSTER_SUSPECT_MS` → `suspect`, another window silent → `dead`, reaped after
  `KGSM_API_CLUSTER_REAP_MS`. So a node we can't probe but that still gossips to us stays `alive` (an
  asymmetric partition resolves for the demonstrably-live node; the refute/re-suspect oscillation can't run
  away) — the honest first cut of the indirect-probe refinement `§2·b` G5 defers.
- **New knobs** (`ApiOptions`, all floored, inert off-cluster): `KGSM_API_CLUSTER_ADVERTISE_URL` /
  `_GOSSIP_URL` (the two-URL split, §2 #13a), `KGSM_API_CLUSTER_GOSSIP_MS` (5s), `KGSM_API_CLUSTER_POLL_MS`
  (the latency poller's cadence, now configurable, 10s), `KGSM_API_CLUSTER_SUSPECT_MS` (30s),
  `KGSM_API_CLUSTER_REAP_MS` (5 min).
- **Self-validated** (§9 P0.5): `RosterMergerTests` (9 facts pin the decision table) + the in-process
  multi-node `GossipConvergenceTests` (seed A→B + B→C converges A to know C with no direct add; a silenced
  node → suspect → dead → reaped; a false-`dead`-about-self refuted via a higher incarnation; a phantom
  never reaches first-hand `alive`; gossip writes zero outbox rows). **712/712 tests green (+14).**

### Added (v0.21.0) — cluster peer foundation (membership + trust, P0)
- **The peer roster + management surface** (`PLAN-peers.md` P0). `PeerEntity` + the `peers` table
  (`PeersStore`, idempotent `CREATE TABLE IF NOT EXISTS` — the live DB shares the audit log, never a
  wipe); `PeersController` grows admin-gated CRUD (`GET`/`POST`/`DELETE`/`PATCH /api/v1/peers`) + the
  `GET /peers/{id}/latency` read alongside the existing `/inbox`.
- **Join-via-seed handshake** (`PeerHandshakeService`) — paste one peer URL → pull its cluster-token-authed
  `GET /peers/identity` (this node mints + presents its own service token) → require an `apiVersion` match
  and the advertised `cluster` capability → persist. Frozen status mapping: `201` / `400 invalid_url` /
  `409 version_mismatch` (`details:{remote,local}`) / `422 peer_not_cluster` / `502 peer_unreachable`.
- **`GET /api/v1/peers/identity`** — this node's identity card (`{nodeId, apiVersion, build, capabilities}`),
  cluster-token authed (token-only, no enabled-peer gate — a joining node has no roster row yet). The
  `cluster` capability is advertised by the capability model (`NodeCapabilities`) when `ClusterEnabled`, not
  a `LeafCatalog` entry (it has no systemd unit).
- **Disable-list gate** (`PeersTableGate` replaces the allow-all seam) — the shared cluster secret is the
  trust boundary, so an unknown validly-tokened node is accepted; only a node explicitly present-and-disabled
  in the roster is rejected (`403 peer_disabled`). Makes trust transitive under a partial topology view
  (`PLAN-peers.md §2` #7/#8).
- **Roster-fed outbox fan-out** (`RosterClusterTargetProvider`) — the bus draws its delivery targets from the
  enabled roster (`GossipUrl ?? Url`) instead of hand-supplied ones.
- **10s latency poller** (`PeerLatencyPoller`) — probes each enabled peer's `/identity` (minting a token per
  tick), recording `reachable`/`unreachable` + latency; honest null latency and an untouched `lastSeen` on
  failure, fail-open per peer.
- **Self-validated** (§9): the add-peer outcome matrix (201/502/422/409 + invalid-url + admin gate), the
  disable-list gate (unknown/enabled/disabled + a real in-process two-node handshake and disable→403→re-enable
  cycle), and `/identity` auth (401/200). Token mint/verify + previous-secret rotation stay covered by
  `ClusterTokenServiceTests`. 698/698 tests green (+18).

### Changed (v0.20.2) — quiet EF command logging
- **`src/Api/appsettings.json`** — set `Microsoft.EntityFrameworkCore.Database.Command` to `Warning`.
  EF logs every executed SQL statement at Information; with the cluster outbox drainer running a
  due-scan every second (once `KGSM_API_CLUSTER_SECRET` is set), that flooded journald with a
  full SELECT line per second. Warning keeps failures/warnings; drop back to Information transiently
  when debugging a query.

### Added (v0.20.1) — cluster message bus two-node self-validation (Phase 4)
- **`tests/Api.Tests/ClusterTwoNodeTests.cs`** — proves the cluster bus (Phases 1–3) across two real
  nodes in-process: `ClusterNodeFactory` (an `AuthTestFactory` re-configured with its own node id,
  host id, cluster secret, drain interval, and DB) plus the routing trick of pointing node A's real
  `OutboxDrainer` HTTP client (`"cluster-outbox-drainer"`) at node B's `TestServer.CreateHandler()` —
  every message crosses the real `PeersController`/`ClusterTokenService`/`SessionRevokeHandler` code
  paths with no sockets involved. Three tests: happy path (A enqueues, B revokes, A's outbox row
  reaches `delivered`); down-then-up (a togglable `DelegatingHandler` returns `503` while "B is down" —
  the row is observed genuinely `pending`/`Attempts>=1` before the toggle flips, then `delivered` after);
  and auth fail-closed (mismatched cluster secrets → B's `401` dead-letters the row, the session on B
  stays untouched). No Phase 1–3 defects surfaced — all three passed against the existing code
  unmodified. 680/680 tests green.

### Added (v0.20.0) — cluster message bus outbox drainer + GC (Phase 3)
- **`ClusterTarget`** (`Services/Cluster/ClusterTarget.cs`) — `record(NodeId, Url)`, the delivery
  address a caller of `IClusterBus.EnqueueAsync` supplies per peer. No `Peers` table yet (a later,
  separate peer-foundation milestone), so the caller passes the target set itself.
- **`IClusterBus` / `ClusterBus`** (`Services/Cluster/`) — the send seam:
  `EnqueueAsync(type, payload, targets, ct)` mints one shared `messageId` per broadcast and writes one
  `OutboxMessage` row per target (`Id = "<messageId>:<targetNodeId>"`, `Status="pending"`), gated the
  same way as `SessionStore`/`ClusterInbox` (an `IServiceScopeFactory` + a write-serializing
  `SemaphoreSlim`). `IClusterBus`'s XML-doc records the deliberate gap: the plan (§6) wants this
  enqueue to share the caller's own DB transaction with the local effect it announces (e.g. cluster
  logout revoking local sessions then enqueuing peer notifications atomically); today it is a
  standalone gated write run right after the caller's local effect commits — narrow crash window,
  accepted because the first transactional caller (cluster logout, `PLAN-peers.md` P1) isn't built
  yet. `ClusterBus` also carries the drainer/GC's store-side helpers (not part of the public seam):
  `ListDueAsync`, `MarkDeliveredAsync`, `MarkTransientFailureAsync`, `MarkDeadAsync`, `PruneAsync`.
- **`OutboxDrainer`** (`Services/Cluster/OutboxDrainer.cs`) — the `BackgroundService` that actually
  sends. Inert (no timer) when `ClusterEnabled` is false; a startup catch-up pass, then a
  `PeriodicTimer` loop (`KGSM_API_CLUSTER_DRAIN_MS`, default 1s) with a per-tick swallow so one bad
  tick never kills it. Per due row (`pending`, `NextAttemptAt<=now`, capped at 100/tick,
  oldest-first): a TTL check first (`KGSM_API_CLUSTER_RETRY_TTL_DAYS`, default 7 — a row this old is
  dead-lettered with a loud log without ever being sent); else rebuilds the `ClusterEnvelope`
  (`from=NodeId`, `ts=CreatedAt`) and POSTs it, freshly bearer-tokened via `IClusterTokenService`, to
  `{TargetUrl}/api/v1/peers/inbox` through a named `HttpClient` (10s timeout, via
  `IHttpClientFactory`). `2xx` → delivered; `400`/`401`/`403`/`413` → dead-lettered with a loud log
  (a LOCAL misconfiguration signal — wrong secret, or the peer disabled us — not a lost message);
  anything else (a thrown exception, a `5xx`, any other status) → transient: `Attempts++`,
  `NextAttemptAt = now + backoff(Attempts)`, stays `pending`. Backoff: capped exponential with jitter,
  `min(5min, 1s · 2^(attempts-1)) + up to 20% jitter`. The latency-poller / `node.online`
  immediate-flush coupling (plan §6, M-bus·b) is deliberately NOT built this phase — the durable retry
  loop is correct without it, just slower to notice a recovered peer.
- **`ClusterBusGcWorker`** (`Services/Cluster/ClusterBusGcWorker.cs`) — mirrors
  `SessionCleanupWorker` exactly (inert when `ClusterEnabled` is false, startup catch-up, then a
  `PeriodicTimer` loop at `KGSM_API_CLUSTER_GC_MS`, default 10 min). Each pass calls
  `ClusterBus.PruneAsync(now - ClusterRetentionDays, now)`, deleting `delivered`/`dead` outbox rows
  and old inbox dedupe-ledger rows; a `pending` row is never pruned regardless of age.
- **Config** (`ApiOptions` + `appsettings.json`): `KGSM_API_CLUSTER_DRAIN_MS` (default 1000, floor
  250), `KGSM_API_CLUSTER_RETRY_TTL_DAYS` (default 7, floor 1), `KGSM_API_CLUSTER_RETENTION_DAYS`
  (default 30, clamped to at least `ClusterRetryTtlDays + 1` so a late redelivery right at the TTL
  boundary is still recognized as a duplicate rather than re-applied), `KGSM_API_CLUSTER_GC_MS`
  (default 600000, floor 60000). All four are non-`required` (defaulted), so the many existing
  test-built `ApiOptions` literals needed no changes.
- **`Startup.cs`**: registers the named `cluster-outbox-drainer` `HttpClient` (10s timeout, via
  `AddHttpClient`), `ClusterBus` as both `IClusterBus` and its concrete type, and hosts both
  `OutboxDrainer` and `ClusterBusGcWorker`.
- **`tests/Api.Tests/OutboxDrainerTests.cs`** — a real temp-file SQLite `AppDbContext` (no
  `WebApplicationFactory`; there is no HTTP endpoint under test here) + a fake `HttpMessageHandler`
  standing in for the peer. Covers: `EnqueueAsync` with 2 targets → 2 correctly-shaped pending rows
  (and a no-targets no-op); a 200 peer response → both rows delivered, the sent envelope's `from`
  equals `NodeId` and the URL ends `/api/v1/peers/inbox`; a 503 → stays pending, `Attempts=1`,
  `NextAttemptAt` in the future, `LastError` set; a 400 → dead; a row seeded past the retry TTL →
  dead-lettered without ever calling the peer; `PruneAsync` deletes old delivered/dead outbox rows
  and old inbox rows while keeping fresh ones and any still-`pending` row regardless of age.
- **Not built this phase** (M-bus·b, per the plan's own phasing): the latency-poller /
  `node.online` immediate-flush coupling — a pure latency optimization; the durable retry loop is
  correct without it.

### Added (v0.19.0) — cluster message bus receive path (Phase 2)
- **`POST /api/v1/peers/inbox`** (`Controllers/PeersController.cs`) — the wire endpoint
  (`docs/cluster-message-bus-plan.md §4`). `[AllowAnonymous]` w.r.t. the user auth scheme (it runs
  regardless of `KGSM_API_AUTH_DISABLED`); does its own fail-closed cluster-token auth inline. Status
  mapping: `401 invalid_cluster_token` (no/invalid bearer), `403 peer_disabled` (the `IClusterPeerGate`
  seam, below), `403 from_mismatch` (`envelope.from` ≠ the token's node id), `413 payload_too_large`
  (over 64 KiB — checked against `Content-Length` up front, then re-enforced by a hard-capped manual
  body read so a chunked request can't lie its way past the header check), `400 bad_request`
  (unparseable JSON or a missing `id`/`type`/`from`), `200 { status: "accepted" }` for a fresh apply, a
  de-duplicated replay, or a dropped-unknown-type message alike (the sender cannot and needn't tell
  them apart), `500 internal` only for a genuinely transient handler failure (so the sender keeps
  retrying).
- **`ClusterEnvelope`** (`Services/Cluster/ClusterEnvelope.cs`) — the §3 envelope record
  (`id`/`type`/`from`/`ts`/`payload`), camelCase, `payload` left as a raw `JsonElement` so each
  handler owns its own payload shape.
- **`IClusterMessageHandler`** (`Services/Cluster/IClusterMessageHandler.cs`) — the discriminated-union
  handler seam (`Type` + `HandleAsync`), registered in DI as a collection and dispatched by `type`.
  Handlers are contractually idempotent (applying twice is harmless) — the safety property the whole
  receive algorithm leans on.
- **`SessionRevokeHandler`** (`Services/Cluster/Handlers/SessionRevokeHandler.cs`) — the first handler,
  `session.revoke`. `scope:"sid"` → `SessionStore.RevokeAsync` + `ISessionValidator.Evict`;
  `scope:"user"` → resolves `discordId` to `discord:<id>`, calls `RevokeAllForUserAsync`, evicts every
  returned sid; `scope:"all"` is logged as RESERVED and no-ops (a node-wide nuke is out of MVP scope); a
  missing/unrecognized field for the given scope logs a warning and no-ops rather than throwing (a
  throw would 500 and wedge the sender into an infinite retry against a payload that can never become
  valid).
- **`ClusterInbox`** (`Services/Cluster/ClusterInbox.cs`) — the §7 receive/dedupe/dispatch algorithm, a
  scoped-singleton store mirroring `SessionStore`'s idiom (an `IServiceScopeFactory` + a write gate).
  Deliberately **handler-first**, not the spec's literal insert-first: a handler's effect (e.g.
  `session.revoke` writing the `sessions` table) lands in a different `AppDbContext` scope/transaction
  than this class's own inbox-ledger write, so there is no single transaction to roll both back
  together. Handler-first is safe precisely because handlers are idempotent — a crash between "handler
  succeeded" and "ledger row committed" just means the next redelivery re-runs a no-op. An unknown
  `type` is recorded (ledger row, `ProcessedAt` left `null`) and dropped rather than 500'd; a handler
  exception is NOT recorded, so the sender's retry re-dispatches from scratch.
- **`IClusterPeerGate` / `AllowAllClusterPeerGate`** (`Services/Cluster/IClusterPeerGate.cs`) — the §4
  "`iss` is an enabled peer" seam. There is no `Peers` table yet (a later peer-foundation milestone), so
  the default implementation treats any node that already presented a validly-signed cluster service
  token as enabled — correct under today's one-guild trust boundary; swap the registration for a
  `Peers`-table-backed implementation when that milestone lands, no controller change needed.
- **`tests/Api.Tests/ClusterInboxTests.cs`** — boots the real pipeline with a configured
  `KGSM_API_CLUSTER_SECRET`/`KGSM_API_NODE_ID`, mints real cluster tokens through the running
  `IClusterTokenService`. Covers: a valid `session.revoke` (scope `sid`) actually revoking the row and
  evicting the validator cache; no bearer → `401`; a token signed with the wrong secret → `401`; a
  `from`/token mismatch → `403`; the same envelope id delivered twice → both `200`, exactly one ledger
  row, the effect applied once; an unknown `type` → `200`, never a `500`.
- **Not built this phase** (Phase 3): the outbox drainer, `IClusterBus.Enqueue`, and inbox/outbox GC.

### Added (v0.18.0) — cluster message bus foundation (Phase 1)
- **`KGSM_API_CLUSTER_SECRET` / `KGSM_API_CLUSTER_SECRET_PREVIOUS` / `KGSM_API_NODE_ID`** —
  the config keys behind the cluster service token (`docs/cluster-message-bus-plan.md`,
  `PLAN-peers.md §3`). Blank secret (the default) ⇒ `ApiOptions.ClusterEnabled` is `false` — this
  host is not part of a cluster. `NodeId` defaults to `HostId` (`PLAN-peers.md §2` #2).
- **`IClusterTokenService` / `ClusterTokenService`** (`Services/Cluster/`) — mints and validates the
  node-to-node service JWT (`sub=node:<id>`, `iss=<id>`, `aud=cluster`, 60s TTL), HMAC-SHA256 signed
  with the cluster secret (distinct from the user-token signing key). Validation accepts the current
  secret or, during a rotation overlap window, the previous one. Fail-closed: any invalid, expired,
  wrong-audience, wrong-signature, or malformed token — or a blank cluster secret — validates to
  `null`, never throws. Registered as a singleton in `Startup`; nothing consumes it yet.
- **`OutboxMessage` / `InboxMessage`** (`Data/`) — the transactional-outbox/inbox schema
  (`docs/cluster-message-bus-plan.md §5`), mapped onto `AppDbContext` as `cluster_outbox`/
  `cluster_inbox` (`EnsureCreated`, UTC-ticks timestamps, the same posture as every other table
  here). `OutboxMessage` adds one field beyond the spec table, `TargetUrl` — denormalizing the
  delivery address onto the row since the `Peers` table (a separate, later increment) doesn't exist
  yet. **No writer or reader exists yet** — this milestone is schema + the auth seam only; the
  `/peers/inbox` endpoint, the outbox drainer, and `IClusterBus` are later phases.
- **`tests/Api.Tests/ClusterTokenServiceTests.cs`** — mint↔validate round-trip, previous-secret
  rotation window, wrong secret / garbage token / expired token / wrong audience all fail closed,
  and a blank cluster secret makes `Mint()` throw and `ValidateAsync` always return `null`.

### Added (v0.17.0) — session GC worker (M4·c Increment 8)
- **`SessionCleanupWorker`** — a new `BackgroundService` that permanently bounds the `sessions`
  table: on a timer (`KGSM_API_SESSIONS_GC_MS`, default 10 min, floor 60s), it bulk-deletes every
  session row whose `Expires` has passed — **both revoked and non-revoked** (an expired row is dead
  regardless of whether it was ever revoked; the 30-day absolute cap already killed it). Runs once at
  startup as a catch-up pass (a host that was down doesn't wait a full interval to start shedding
  rows), then on the `PeriodicTimer`. **Inert when `KGSM_API_SESSIONS_DISABLED=1`** (the master
  switch) — logs once and returns with no timer at all, matching the registry's "whole thing is
  off" posture.
- **`SessionStore.DeleteExpiredAsync(DateTimeOffset now)`** — the new store method backing the
  worker; a single indexed `ExecuteDeleteAsync` bulk delete (EF Core, .NET 10) on `ix_sessions_expires`,
  serialized on the store's existing write gate (same posture as every other `SessionStore` write) so
  it can't race a concurrent login/refresh write on SQLite's single writer. Returns the deleted count.

### Added (v0.16.0) — `/me.recentLogins` + mint-time `expiresAt` (M4·c Increment 7)
- **`GET /me` gains `recentLogins[]`** — the last 10 `auth.login` audit rows for the caller (`{ ts,
  device }`, newest first), each `device` sourced from the login's `User-Agent` header. This is
  `/me`'s first DB read (every other field is projected off the bearer's claims, no I/O); it
  complements — and reads a different source than — `GET /auth/sessions` (Increment 6, the live,
  revocable registry): recentLogins is append-only provenance ("what happened"), sessions is current
  state ("what's active now"). A fresh actor with no prior login gets `[]` (honest, never fabricated).
- **`auth.login`'s audit `meta` now also carries `userAgent`** (alongside the existing `tier`/`sid`) —
  the login-time UA is threaded from the same value already written to the `SessionEntry` row.
  Additive to the existing direct audit write; not a new writer or a new action (invariant #5 intact).
  `auth.logout` is unaffected (still `tier`+`sid` only).
- **`CallbackResult` gains `accessTokenExpiresAt`/`refreshExpiresAt`** (both `DateTimeOffset?`,
  omitted-when-null) — the mint-time expiry of each token, so the SPA can learn expiry without
  decoding the JWT. Omitted on every `"denied"` response (no tokens are minted there), so that
  branch's wire shape is byte-for-byte unchanged.
- **`RefreshResponse` gains `expiresAt`** (non-nullable) — the new access token's mint-time expiry,
  letting the SPA schedule a proactive re-refresh instead of reacting to a `401`.
- Both are tail-additive fields on existing DTOs (invariant #4); no existing consumer breaks.

### Added (v0.15.0) — session revocation endpoints (M4·c Increment 6)
- **`GET /auth/sessions`** — the caller's own active-session set (id, created/lastSeen/expires,
  user-agent, a `current` flag on the calling bearer's own session), or, for an admin passing
  `?userId=`, another user's active set. Reads the registry (`SessionEntry`), never the audit log —
  the honest "Active Sessions" source per D3.
- **`POST /auth/session/revoke`** (self) — `{ sid? }` revokes one of the caller's own sessions
  (`404` if the sid doesn't belong to them — never leaks whether it exists for someone else),
  `{ all: true }` revokes every session the caller owns including the calling one ("log out
  everywhere"), and neither field revokes the calling session (logout-equivalent). Both fields set
  at once → `400`.
- **`POST /auth/sessions/{sid}/revoke`** (admin) — revoke any session cross-user by id (D4). `404`
  on an unknown sid.
- **`POST /auth/users/{userId}/sessions/revoke-all`** (admin) — "log out this user everywhere."
  Always `204`, including when the user has no active sessions.
- Every revoke evicts the session from the per-request validator cache (same ~instant-revoke
  posture as `POST /auth/logout`) — the 5s cache TTL (D2) is only the backstop.
- Three new direct-write audit actions (no kgsm event backs a revocation, so this is the single
  writer — the same posture as `auth.login`/`auth.logout`, no double-write): `auth.session.revoke`
  and `auth.session.revoke.all` (self-service, info), `auth.session.revoke.admin` (an admin acting
  on another user's session(s) — warn, covering both admin endpoints).

### Added (v0.14.0) — rolling refresh tokens + session revocation (M4·c Inc 4·b)
- **Rolling (sliding) refresh window.** `POST /auth/session/refresh` now slides the session's
  `Expires` forward to `now + KGSM_API_SESSIONS_REFRESH_ABSOLUTE_DAYS` (default 30d) on every
  successful refresh and bumps `LastSeen`. A session used at least once inside the window stays
  logged in indefinitely; an idle session still dies N days after its last use. (Supersedes the
  M4·c plan's D8 "no sliding" — user directive.)
- **Refresh-token rotation + reuse detection.** Each token now carries a per-token `jti` claim, and
  the session row stores the current refresh token's jti (`SessionEntry.CurrentJti`). A refresh
  rotates **both** tokens; presenting a stale/old/reused refresh token (its `jti` no longer matches
  the row's `CurrentJti`) → `401`. (Supersedes the plan's D9 "no rotation".)
- **`POST /auth/logout` now revokes server-side.** It flips the session row `Revoked=true` and evicts
  the validator cache, so every token on that `sid` (access + refresh) stops authorizing (~instant
  via eviction; ≤15min access-TTL hard ceiling). Previously logout was client-side-only.

### Changed (v0.14.0) — BREAKING (wire)
- **`RefreshResponse` gains a `refresh` field.** `{ token, refresh, tier }` (was `{ token, tier }`).
  The SPA MUST adopt the rotated `refresh` token on every refresh call — the previously-held refresh
  token is dead after one use (reuse detection). A pre-rotation refresh token (no `jti` claim) is
  rejected with `401` (the same clean-break posture as the M4·c `sid` check).
- **Migration:** the existing prod DB needs a one-shot
  `ALTER TABLE sessions ADD COLUMN "CurrentJti" TEXT` (fresh DBs get it via `EnsureCreated`). Rows
  created before this ships have a null `CurrentJti` and adopt the first presented jti on their next
  refresh.

### Changed (v0.13.0)
- Player roster identity is now **name-first**: the durable person key resolves as
  `id → name → addr → sessionKey` (was `id → addr → name → sessionKey`). For
  account-less direct-socket games (e.g. romestead) the character **name** now keys the
  roster instead of `ip:port` — a reconnect from a new ephemeral port, or after an ISP IP
  change, no longer mints a duplicate roster row. The name is present on both join and
  leave (the watchdog's `PlayerSessionMap` backfills it onto an `addr`-only leave line), so
  leave still correlates to the right person-row. Resolution is consolidated into a single
  `PlayerIdentityResolver` (was duplicated across four sites); the name is trimmed but not
  case-folded. The session-level key (`PlayerRosterService.ResolveKey`) is unchanged — a
  session is still keyed on the connection. (player-presence-contract.md §5.)
- On startup, `PlayerHistoryService` runs a one-time idempotent **re-key + merge** of the
  existing roster: every row is regrouped onto its recomputed person key, and rows that
  collapse together (old addr-first duplicates of the same character) are merged into one
  survivor — earliest `FirstSeen`, latest `LastSeen`, `banned` status/reason never lost,
  freshest non-blank name/addr/id carried forward.

### Added (v0.12.0)
- New server run-state `starting`, distinct from `running`: the window between
  `instance_started` (process spawned) and `instance_ready` (the watchdog's
  log-scrape confirms the game finished booting) — both events observe the process
  as "up," so the distinction is tracked out-of-band by a new `InstanceCache`
  "starting latch" (`MarkStarting`/`MarkReady`/`IsStarting`), not derivable from the
  boolean run-state reading alone.
- `KgsmAuditConsumer` registers a handler for the new `instance_ready` event
  (kgsm-lib 1.35.0, `InstanceReadyData`) — audit-silent by design (a run-state
  refinement of the already-recorded `server.start`, not a new fact); it only
  clears the starting latch.
- `ServerAggregator.BuildServer` folds the starting latch into `Server.status`
  (`ServerStatus.Starting = "starting"`); `DomainPump`'s existing status diff fans
  `starting`/`running` transitions out over the `servers` SSE topic with no pump
  change.
- `InstanceCache`'s background boolean reconcile can no longer promote
  `starting → running` on its own (the reconcile-hazard guard,
  `ReconcileStartingLatch`) — it only ever closes the latch on new evidence (the
  process measured down, the instance vanished from the roster, or a 5-minute
  safety timeout, which resolves honestly to `running` since the process is
  observed up in that same pass).
- `CommandGate`: `start` against a `starting` server is now inadmissible (409,
  same no-op class as start-when-running); `update` against `starting` is
  inadmissible (same "files in use" reason as running); `stop` against `starting`
  remains admissible (an operator can abort a server stuck mid-boot).

### Added (v0.11.0)
- `Job` DTO gains `phase` (install sub-phase: `"preparing"` | `"downloading"` | `"deploying"`)
  and `blueprint` fields; both are null for non-install jobs.
- `job.patch` SSE frames for install jobs now carry `blueprint` (stamped immediately in
  `StartInstall` before the background task runs) so any connected user can create a phantom card.
- `KgsmAuditConsumer` handles `instance_installation_started`, `instance_download_started`, and
  `instance_deploy_started` events and emits `job.patch` SSE frames with the corresponding
  `phase` value so clients show granular install progress.

### Added (v0.10.0)
- `GET /servers/{id}/settings`: `crashRestart` (bool) + `crashMaxRestarts` (int) from
  instance config (null when the kgsm config key is unset).
- `PATCH /servers/{id}/settings`: `crashRestart` (bool) + `crashMaxRestarts` (int, 1–10)
  → `crash_restart` / `crash_max_restarts` config keys. Validation: crashMaxRestarts
  must be 1–10.
- kgsm-lib → 1.35.0 (Instance.CrashRestart, CrashMaxRestarts).

### Added (v0.9.0)
- `GET /servers/{id}/settings`: `autoBackupOnRestart`, `backupRetention` from instance
  config; `lastBackupUtc`, `lastBackupOk` from scheduler status socket (null when
  scheduler leaf absent or no backup run yet).
- `PATCH /servers/{id}/settings`: `autoBackupOnRestart` (bool) + `backupRetention`
  (int, 1–100). Validation: retention must be 1–100; auto-backup=true requires a
  non-off scheduled cadence.
- kgsm-lib → 1.34.0 (Instance.AutoBackupOnRestart, BackupRetention, PruneBackups).

### Added
- **Settings Phase 3 — Scheduled restart.** `GET /servers/{id}/settings` now includes
  `scheduledRestart`, `restartTime`, `restartDay`, `timezone` (from kgsm instance config)
  and `nextFireUtc` (from the scheduler leaf status socket, null when scheduler absent).
  `PATCH /servers/{id}/settings` accepts all four schedule fields with validation.
  New `scheduler` leaf registered in `LeafCatalog` + `LeafHealthMonitor`; degrades
  gracefully when the scheduler daemon is absent (nextFireUtc null, scheduled-tasks
  card gated in the SPA). New `SchedulerClient` reads the NDJSON-over-unix-socket status
  snapshot at `KGSM_API_SCHEDULER_SOCKET` (opt-in — blank default). kgsm-lib upgraded to 1.33.0.
- **Settings Phase 2 — Resources.** `GET /servers/{id}/settings` now includes `cpuPriority: string|null`
  and `memoryCapMb: int|null`. `PATCH /servers/{id}/settings` accepts both fields: `cpuPriority`
  (low/normal/high — validated, live-applied via `IWatchdogClient.SetCpuPriorityAsync`, best-effort)
  and `memoryCapMb` (≥0, 0=uncapped — persisted to kgsm config, takes effect at next restart).
  kgsm-lib upgraded to 1.32.0.
- **`GET/PATCH /api/v1/servers/{id}/settings` (Phase 0 — Settings spine).** New settings aggregator
  endpoint, operator-gated write. Phase 0 surfaces the `autoUpdate` toggle (the existing `auto_update`
  kgsm config key). Later phases add autostart, resource caps, and scheduler config as those primitives
  land. Follows the `ServerConfigController` pattern: echo-path audit (kgsm's `instance_config_changed`
  event carries provenance), no double-write.

### Changed
- **Settings Phase 1 — Autostart.** `GET /servers/{id}/settings` now includes `autostart: bool|null`
  (null when the watchdog is absent/unreachable — honest unknown, never fabricated). `PATCH
  /servers/{id}/settings` accepts `autostart: bool` and fans out to `IWatchdogClient.Enable/Disable`
  (503 when the watchdog is not provisioned; 400 on a watchdog refusal). kgsm-lib upgraded to 1.31.0.
- **Uninstall pre-stop (Phase 0 — delete hardening).** `CommandRunner.RunUninstall` now issues a
  best-effort `Stop` before `Uninstall`, so we never orphan a running process. A non-zero stop result
  (instance already stopped) is logged at Debug and ignored.
- **Blueprint catalog cached in-memory (60s TTL, background refresh).** `GET /library` no longer
  spawns a `kgsm.sh` process on every request — a singleton `BlueprintCache` serves the blueprint
  dictionary from memory, refreshed by a background `PeriodicTimer` every 60s (configurable via
  `KGSM_API_BLUEPRINT_CACHE_TTL_SECONDS`). First request triggers an on-demand load; subsequent
  reads are instant. The `LibraryHydrationWorker` shares the same cache instead of making its own
  process spawn per sweep.

## [0.4.1] - 2026-07-02

### Fixed
- **SSE write loop no longer busy-loops (100% CPU) after a client disconnects.** On disconnect the
  connection token (`RequestAborted`) is cancelled; the wake branch of `StreamConnection.WriteLoopAsync`
  never checked it, so `await Task.WhenAny(canceledWait, canceledDelay)` completed synchronously every
  iteration and the loop drained an empty queue and `continue`d forever without yielding — one orphaned
  ThreadPool thread pegged at 100% per disconnected stream. The loop now guards on the token (loop
  condition + a post-`WhenAny` break) and cancels the losing task each iteration via a linked CTS (which
  also stops a 20s heartbeat timer from being abandoned on every wake). Regression-tested
  (`StreamConnectionTests`): a cancelled token stops `RunAsync` promptly, never spins.

## [0.4.0] - 2026-07-02

### Changed
- **Realtime transport migrated from WebSocket to Server-Sent Events (SSE).** `GET /api/v1/stream`
  is now a `text/event-stream` GET: topics are chosen via `?topics=` (resource-scoped topics
  contain `/`), the bearer arrives in the `Authorization` header, and an auth failure returns a
  plain, readable `401` (no more opaque WebSocket `1006` close). Server pushes `data:` frames with
  a `: connected` preamble and a 20s `: keepalive` heartbeat; `X-Accel-Buffering: no` +
  `DisableBuffering()` keep frames unbuffered through reverse proxies. The per-connection
  coalesce-to-latest queue and subscriber-gated publishers are unchanged — only the wire transport
  and framing changed. Removed the client→server command channel (`Subscribe`/`Unsubscribe`/`Ping`/
  `Pong`) since topics are now fixed at connect time by the query string.
- **HTTP protocol bumped to `Http1AndHttp2`** (prod TLS negotiates h2; dev plain-text stays h1.1).
  The prior h1.1 lock existed solely because WebSocket-over-h2 has no path in Kestrel; with WS gone
  that constraint is lifted. `UseWebSockets()` and the `?access_token=` query-string auth shim are
  removed from the pipeline.

## [0.3.1] - 2026-07-01

### Changed
- Player roster startup: replaced `MarkUnknownOnStartupAsync` (mark all online → unknown) with
  `ReconcileFromWatchdogAsync` — queries the watchdog's `GET /players` endpoint for the live
  session snapshot, marks matching players online and everyone else offline. No intermediate
  unknown state. Falls back to marking unknown when the watchdog is absent/down. Handles new
  players who joined while the API was down (inserted as online). Bumped kgsm-lib to 1.30.0.

## [0.3.0] - 2026-07-01

### Added
- Permanent player roster history: `PlayerHistory` entity + `PlayerHistoryService` (DB-backed
  authority for who has ever connected to a server). Each player has a `PlayerStatus` (online,
  offline, banned, unknown) that is deterministic — status resolves only on the next event,
  never probed. `MarkUnknownOnStartupAsync` marks all online players unknown on API restart.
  `players.ban` support with `banReason`. 14 new `PlayerHistoryServiceTests`.

## [0.2.0] - 2026-07-01

### Added
- Metrics-threshold alert source (increment 1) — built + unit-tested + live-validated (2026-07-01; not yet deployed): the
  `host-monitor`/`metrics` `AlertSource`s — previously reserved — are wired into the existing `AlertEngine`
  (new `TickMetrics` reconcile) so a sustained host or per-server metric breach (disk/mem/swap/load/temp on
  the host; opt-in pids/mem/cpu per server) raises/resolves through the same `/alerts` REST + `alerts` WS
  surface as the crash source. Two dwells (fire-dwell + clear hysteresis), honest-unknown on a down monitor,
  `resolution.actionId` always `null` (the audit bridge is crash-specific), metric `danger` never escalates.
  Policy is appsettings + env (`MetricsThresholds`), not yet DB-backed (increment 2). New `AlertSurface.Host`
  constant. 10 new `TickMetrics` tests; full suite 474/474. See `metrics-threshold-alerts-plan.md` and
  `src/Api/Services/Alerts/CLAUDE.md`.

### Fixed
- `MetricsThresholds:Rules` config binding no longer silently drops a custom policy back to the baked-in
  default when any rule is warn-only (`danger` absent/null): the binder cannot construct the positional
  `ThresholdRule` record without a `Danger` value, so binding now goes through a mutable `ThresholdRuleBinding`
  DTO. (Latent: the default policy's own warn-only `srv-*` rules made every operator override fall back to
  `Default`.)
- The crash `AlertEngine.Tick` resolve/retract loop now only reconciles `crash:` ids — the metric source
  shares the firing set, and without the guard a watchdog poll would wrongly retract/resolve a live
  `metric:` alert.

## [0.1.0] - 2026-06-30

### Added
- Initial versioned release.
