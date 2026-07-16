# KGSM Cluster — Peer Federation (Constellation)

> Living design doc. The mesh that lets independently-deployed `kgsm-api` nodes,
> all belonging to **one Discord guild**, discover each other's resources,
> recommend placement, share a single sign-on, and federate the assistant.
>
> Extends the O7 stub in `system-architecture.md §5`; companion to
> `kgsm-api/PLAN.md` (the per-host milestone plan). The node-to-node transport it
> rides on has its own authority: **`docs/cluster-message-bus-plan.md`** (built
> first — it is the foundation every peer feature depends on).

---

## Status legend
`built` = exists & verified · `partial` = exists, incomplete · `planned` =
designed, not built · `open` = not yet decided.

---

## 0 · The security boundary (read this first)

**A cluster is one Discord guild, and therefore one trust domain, single-owner.**
Every node in a cluster is configured with the **same** Discord application, the
**same** `KGSM_API_AUTH_SIGNING_KEY`, and the **same** `KGSM_API_AUTH_ROLE_*`
tier map. This is a hard cluster-formation precondition, not an implementation
detail:

- It is what makes cross-node authorization coherent — a user's tier is identical
  on every node, so "operator on the node I logged into" means "operator
  everywhere."
- It is what makes single sign-on possible — a session vouched by one node is
  honored by the others because they share the guild and the signing key.
- **The cost, accepted eyes-open:** the nodes are not security-independent. A
  fully-compromised node can mint or vouch identities cluster-wide (it already can
  forge user tokens, because the HMAC signing key is shared). Treat the whole
  cluster's blast radius as the blast radius of its weakest node. This is
  acceptable **only** because a cluster is single-owner by definition. A
  multi-owner mesh is out of scope and would require per-node asymmetric identity
  (see §3, mTLS upgrade path).

This does **not** break the "leaves independently deployable" doctrine
(`system-architecture.md §4`): a node still runs fully standalone; `cluster` is an
additive capability. Membership requires config homogeneity; operation does not.

---

## 1 · Terminology

| Concept | Term | Scope |
|---|---|---|
| A federation of nodes | **Cluster** | SPA page, all docs, all code |
| A single kgsm-api deployment | **Node** | Everywhere a deployment is referenced |
| Node capability set | **Node capabilities** | §4·b capability model |
| SPA connection registry | `localStorage` **nodes** | SPA persistence |
| The cluster overview page | **Cluster page** | SPA (replaces the Fleet route) |

**Node** = a kgsm-api deployment that provisions the `cluster` capability. A
kgsm-api that does not advertise `cluster` is **invisible** to the mesh — not
queryable, not listed, not discoverable. `cluster` is a **gate**: participation is
binary (in or out). Node health on the Cluster page is derived from latency +
per-leaf status, not from a status line on the capability itself.

---

## 2 · Design decisions (locked)

### Identity, trust & transport

| # | Decision | Choice |
|---|---|---|
| 1 | Peer transport | REST over HTTPS on the existing API surface |
| 2 | Node identity | Config-driven `nodeId` (default: machine name, same as HostId) |
| 3 | Cluster membership proof | A shared **`KGSM_API_CLUSTER_SECRET`** (HMAC), distinct from the user JWT signing key |
| 4 | Node-to-node auth | A **service JWT** signed with the cluster secret (`sub=node:<id>`, `aud=cluster`, `iss=<id>`, short TTL) |
| 5 | No per-node keypairs | Symmetric shared secret only — no Ed25519, no per-request asymmetric signing |
| 6 | Handshake | Admin pastes a URL → local node pulls the remote `/identity` over TLS → stores it. No key exchange, no fingerprint confirmation |
| 7 | Trust direction | **Symmetric by construction** — the shared secret *is* the trust boundary. A node trusts any caller bearing a valid cluster-secret service token whose `iss` is **not an explicitly-disabled peer** (a **disable-list** gate, not an allow-list): an unknown-but-validly-tokened node is trusted, because holding the secret already proves guild membership. This is what makes trust transitive without pairwise handshakes and lets the mesh work under a partial topology view |
| 8 | Node disable | A row flag in the `Peers` table — disabled ⇒ its service tokens rejected (`403 peer_disabled`); stays in DB for re-enable. Disable is the **only** local override to the shared-secret trust; absence from the table is *not* rejection |
| 9 | Secret rotation | Dual-secret overlap window: nodes accept `{current, previous}`, roll one at a time, drop `previous` |
| 10 | Version policy | **Match on `apiVersion` (`v1`)**, not build version — allows rolling upgrades across a heterogeneous-build mesh |
| 11 | Discovery | **Join-via-one-seed + gossip convergence.** The admin's only membership action, ever, is "join the cluster": paste **one** existing member's URL. From that seed the roster converges automatically (§2·b) — add one, join all. No per-peer approvals, no master node |
| 12 | Peer + secret storage | SQLite (`Peers` table = the durable roster + seed set; secret from config/env, never stored) |
| 13a | Advertised client URL | Each roster entry carries a **browser-reachable** URL (what the SPA uses), which may differ from the node-to-node gossip URL — a node behind LAN/VPN gossips over one address but must advertise a client-reachable one, or "add one, see all" silently breaks in the browser. Collapse to one field when they are equal (§8, §10 #2) |

### Membership & discovery — gossip (locked)

The mesh is masterless: every node is an equal peer, membership converges by
**anti-entropy gossip** — a hand-rolled minimal subset of SWIM + Serf's push-pull
sync, built from the building blocks the API already runs (an `IHostedService` +
two controller endpoints), **no new service, broker, or dependency**.

| # | Decision | Choice |
|---|---|---|
| G1 | Convergence | **Random-peer push-pull anti-entropy**: each interval a node picks **one** random member and exchanges rosters, merging. O(1) work per node per round, O(log n) rounds to converge — never all-to-all (that O(n²) probe storm is the only thing that doesn't scale; roster *size* is trivial) |
| G2 | Conflict resolution | **Incarnation numbers** (SWIM): each node owns a monotonic counter for itself; state is ordered by `(incarnation, state-precedence)`. Only a node can raise its own incarnation, so it **refutes** a false `suspect`/`dead` about itself — no node can kill another by gossip |
| G3 | Hearsay is provisional | A node learned only by gossip is inserted `suspect`/`joining` and promoted to `alive` **only when this node directly authenticates it** (shared-secret handshake, first-hand). Neutralizes phantom-node injection without a master; honesty-clean — an unverified peer is never shown `alive` |
| G4 | Transport split | Gossip rides a **separate ephemeral, best-effort** path (`POST /api/v1/peers/sync`, cluster-token authed, fire-and-forget, **no** outbox row) — never the durable outbox (which is 7-day-retained for guaranteed messages like `session.revoke`; durably retrying a stale ping to a corpse is exactly wrong). The durable bus takes its fan-out target list *from* the converged roster |
| G5 | Failure detection | A **last-evidence clock**: each node times its own evidence for each peer (no cross-node wall-clock). Evidence is **mutual** — our own successful probe OR an authenticated inbound sync FROM the peer — so a node we can't reach but that still gossips to us stays `alive` (an asymmetric partition resolves for the demonstrably-live node; no refute/re-suspect oscillation). No evidence for `ClusterSuspectMs` → `suspect`, another window silent → `dead`, reaped after `ClusterReapMs`. SWIM **indirect probe** (`ping-req` via k members) is a further refinement, deferred until flapping shows up |
| G6 | Scale ceiling (honest) | Full-roster push-pull is O(n) bytes/sync (~20 KB at 100 nodes — fine into the low thousands). Past that, move to delta/Merkle anti-entropy. 100 is inside the simple version's comfort zone; build simple, note the seam |

### Single sign-on across the cluster

| # | Decision | Choice |
|---|---|---|
| 13 | One login, whole cluster | A user authenticates **once** at any node (all nodes are equal); that grants cluster-wide access, tier-gated |
| 14 | Per-node native sessions | Each node mints its **own** session (own `sid` in its own registry) so per-node revocation keeps working |
| 15 | Provisioning mechanism | **Lazy vouch-on-first-use** — the SPA hits a node it has no session for, that node's session is transparently provisioned, retry. Eager pre-warm is an optional later optimization |
| 16 | Vouch endpoint | `POST {node}/auth/cluster-session` — service-token authed, carries the vouched identity, the target mints its own session and returns it |
| 17 | SPA connection model | Unchanged: a **direct multi-host client** — per-node session, per-node SSE, browser fans out. SSO just automates the N logins |
| 18 | Session presentation | The Active-Sessions UI **aggregates per device** across the cluster (one row = "this browser, cluster-wide"), not N rows per login |
| 19 | Logout everywhere | A cluster-wide fan-out over the message bus (`session.revoke`) — durable, so a node that is down when you log out is revoked when it returns |

### Placement & failure

| # | Decision | Choice |
|---|---|---|
| 20 | State sharing | On-demand fan-out; validate-at-use; no cross-node state store |
| 21 | Data shapes | Reuse existing Host/Server/Library DTOs |
| 22 | Capacity inputs | Blueprint-declared RAM/disk (where present) + the target node's live free disk/RAM + current CPU saturation |
| 23 | Capacity honesty | Undeclared requirement ⇒ **"unknown fit," never a guess.** Placement = `free disk ≥ declared AND free RAM ≥ declared`, else `unknown`. CPU is a coarse "is the node already saturated" gate, not a fit prediction |
| 24 | Placement race (TOCTOU) | **Accept the race + honest failure** at start time (MVP). Soft reservations deferred |
| 25 | Availability failures | **Fail-open**: an unreachable/slow/silent peer degrades to an honest "unknown," never a 500 |
| 26 | Auth/authz failures | **Fail-closed**: an invalid service token, a failed tier check, an unverifiable identity ⇒ reject (`4xx`). Distinct code path from #25 — never conflate the two |

---

## 3 · Trust & auth model

```
Cluster secret  KGSM_API_CLUSTER_SECRET   (HMAC, shared by every node in the guild)
JWT signing key KGSM_API_AUTH_SIGNING_KEY (HMAC, shared — signs user tokens)
                └─ deliberately two secrets: leaking the cluster secret does not
                   also hand over user-token forgery, and vice-versa.
```

### Node-to-node call

```
┌── Node A ──────────────────────────────────────────┐
│  Wants: GET {B}/api/v1/peers/self/resources          │
│  Mints a service JWT:                                 │
│    { sub:"node:A", iss:"A", aud:"cluster", exp:+60s } │
│    signed with KGSM_API_CLUSTER_SECRET                │
│  Sends: Authorization: Bearer <service JWT>           │
└───────────────────┬───────────────────────────────────┘
                    ▼
┌── Node B ──────────────────────────────────────────┐
│  1. Verify service-JWT signature (cluster secret,     │
│     current OR previous during a rotation window)     │
│  2. aud == "cluster"                                  │
│  3. iss ("A") is a row in Peers AND enabled           │
│  4. Not expired                                       │
│  → authorized as peer A. Execute + return.            │
│  (Auth failure at any step ⇒ 401/403, fail-closed.)   │
└───────────────────────────────────────────────────────┘
```

There is **no per-node keypair and no per-request Ed25519 signature.** The
node-to-node surface is read-only GETs plus the message-bus inbox; TLS provides
channel security, the service token provides membership + attribution, and the
message-bus dedupe id (`docs/cluster-message-bus-plan.md`) provides replay safety
where it matters. Ed25519-per-request was considered and dropped: it hardened node
identity while user identity stayed forgeable under the shared HMAC key — an
inconsistent, unjustified cost.

### User single sign-on (vouch)

```
User → Node A: Discord OAuth round-trip (only A talks to Discord)
  A mints its own session (sid_A in A's registry) + returns tokens to the SPA.

Later the SPA needs Node B (renders B's data / issues a B action):
  SPA → B with no B session → 401
  SPA → A: "vouch me a session on B"     (or A proactively pre-warms)
  A → POST {B}/auth/cluster-session       (service token + { discordId, roles, disp })
  B trusts the assertion (cluster member) → mints sid_B in B's own registry → returns tokens
  SPA stores B's tokens; retries the B call.
```

- Only the **login node** contacts Discord. Peers mint from the vouched assertion.
- Every node's session is **native** to that node — its own `sid`, its own
  sliding-window refresh, its own revocation authority. `sid`-based revocation
  (the `SessionEntry` registry) keeps working per node.
- **Keep only the active node's session warm.** Idle-node sessions may lapse and
  are re-provisioned lazily on next use — do not run N background refresh loops.

### Compromise & rotation

- **Disabled node:** its service tokens are rejected (`403`); it stays in the
  `Peers` table for one-click re-enable.
- **Stolen cluster secret:** rotate `KGSM_API_CLUSTER_SECRET` across all nodes via
  the dual-secret overlap window (#9). The `Peers.enabled` gate is an operational
  on/off, **not** a cryptographic boundary against a stolen secret (a thief can set
  `iss` to any enabled peer) — rotation is the real remedy.
- **Upgrade path (documented, not built):** if a multi-owner or large mesh ever
  becomes a requirement, replace the shared secret with **mTLS + a shared CA** —
  per-node certs give granular CRL revocation without a cluster-wide rotation. Out
  of scope while clusters are single-owner.

---

## 4 · Peer health (latency)

- Each node polls every **enabled** peer's `GET /peers/{id}/latency` on the
  `ClusterPollMs` interval (default 10s), stores `latencyMs` + `status`.
- No response within timeout ⇒ `status: "unreachable"`. Disabled ⇒ no ping,
  `status: "disabled"`.
- **Two axes, never conflated (§2·b, P0.5):** the poll feeds the first-hand `Status`
  axis (reachable/unreachable/unknown) AND — on a successful probe of a peer that
  authenticates (`/identity` advertises `cluster` + a matching `apiVersion`) — promotes
  that peer to `alive` and stamps the last-evidence clock the gossip failure detector
  reads. `Status` is this node's own probe; `MembershipState` is the gossip-converged
  state; a latency/metrics row is never itself a status.
- The Cluster page reads `GET /peers` (includes `latencyMs`, `status`, and
  `membership`). Per-leaf health of each peer rides the SPA's existing per-node
  `capabilities` SSE — no new SSE topic.
- The poller doubles as the **message-bus liveness signal**: an
  `unreachable → reachable` flip triggers an immediate outbox flush toward that
  peer (see the bus spec).

---

## 5 · Assistant federation (P3)

| # | Decision | Choice |
|---|---|---|
| A1 | Awareness | Always aware of peers (cached), acts only on local shortfall |
| A2 | Cross-node execution | Recommend only — the user confirms on the target node |
| A3 | Cross-node memory | On-demand live queries; no persistent cross-node context |
| A4 | Peer tools | Existing tools gain an optional `nodeId` parameter |
| A5 | Auth for peer queries | Relay: the local API proxies with a **service token**; no user token leaves the origin |
| A6 | Wire path | Assistant → local API → service-token call to peer → result back |
| A7 | Freshness | Real-time query, always live |
| A8 | Peer query failure | Single retry, then honest failure (`peer_unreachable`); a dead peer yields a **partial** cluster answer, never a whole-cluster failure |

Tools gaining an optional `nodeId`: `get_servers`, `get_library`,
`get_host_status`; plus a new `get_cluster_overview` (fan-out aggregate). Routing
is entirely API-side — the assistant only learns the parameter.

---

## 6 · Phased delivery

> **P-1 (foundation, its own spec) — Cluster message bus · `planned`.**
> The transactional outbox/inbox transport. **Built first.** Everything below
> assumes it. Authority: `docs/cluster-message-bus-plan.md`.

### P0 — Peer foundation (membership + trust) · `planned`
The trust half already exists (the message-bus foundation built the
`KGSM_API_CLUSTER_SECRET` config + the `ClusterTokenService` mint/verify seam with
current+previous rotation). P0 adds the **membership** half — a manually-seeded
mesh that works fully before gossip lands:
- `Peers` table (id, url — the advertised client URL, gossipUrl?, nickname, nodeId,
  incarnation, status, membershipState, stateChangedAt, latencyMs, lastSeen,
  apiVersion, enabled). `status` (first-hand probe) and `membershipState`
  (gossip-converged, P0.5) are the two liveness axes, never conflated.
- `cluster` capability advertised by the **capability model** (present when
  `ClusterEnabled`), **not** a `LeafCatalog` entry — it has no systemd unit, so it
  must not render a phantom Services-board card.
- `PeersController`: CRUD + `GET /peers/identity` (`{nodeId, apiVersion, build,
  capabilities}`) + `GET /peers/{id}/latency`.
- Handshake / **join-via-seed** (paste one member's URL → pull `/identity` →
  `apiVersion` match → confirm it advertises `cluster` → store); reachability
  validated at add time.
- The **disable-list gate**: replace `AllowAllClusterPeerGate` with a `Peers`-table
  gate that rejects only explicitly-disabled peers (`403 peer_disabled`); an
  unknown validly-tokened node is accepted (§2 #7).
- Real outbox fan-out: a `ClusterTarget` provider reading enabled roster members
  (the **durable** send set narrows to first-hand-`alive` peers once gossip can inject
  hearsay into the roster — see P1).
- Latency poller (10s), feeding bus liveness.
- **Self-validated:** add a peer (reachable → stored; unreachable → `502`;
  non-cluster → `422`; version mismatch → `409`); mint+verify a service token;
  verify a `previous`-secret token in a rotation window; reject a disabled peer
  (`403`).

### P0.5 — Membership convergence (gossip) · `built`
Promotes the manual mesh into "add one, join all." Builds on P0's tables; no new
service or dependency (§2·b).
- Anti-entropy push-pull loop (`GossipWorker : BackgroundService`, one random enabled
  non-terminal peer each `ClusterGossipMs` round; inert when `!ClusterEnabled`) +
  incarnation numbers (G1/G2). The pure merge core is `RosterMerger.Decide` — strictly
  higher incarnation always wins (refutation), equal incarnation breaks by state
  precedence, and **fresh first-hand evidence outranks equal-incarnation hearsay**;
  self-refutation raises `SelfIncarnation`.
- The ephemeral `POST /api/v1/peers/sync` roster-exchange endpoint — cluster-token
  authed + disable-list gated, fire-and-forget, leaves **no** `cluster_outbox` row (G4).
- **Two liveness axes, never conflated:** `Status` (this node's first-hand probe:
  reachable/unreachable/unknown, poller-owned) and `MembershipState` (the gossip-converged
  SWIM state). A gossip-learned peer is hearsay-provisional — displayed `joining` until
  this node authenticates it first-hand (poller pulls its `/identity`, checks `cluster`
  cap + `apiVersion`, then promotes to `alive`, G3).
- **Failure detection = a last-evidence clock**, evidence from either direction (our probe
  succeeding OR an authenticated inbound sync FROM the peer): no evidence for
  `ClusterSuspectMs` → `suspect`, another `ClusterSuspectMs` silent → `dead`, then reaped
  after `ClusterReapMs`. Mutual evidence means a node we can't probe but that still gossips
  to us stays `alive` (an asymmetric partition resolves for the demonstrably-live node, and
  the refute/re-suspect oscillation can't run away) — the honest first cut of the SWIM
  suspicion+indirect-probe refinement G5 defers.
- **SPA-facing read (G1):** `GET /api/v1/peers/roster` — the **viewer-gated** projection of
  the converged roster the browser reads to auto-populate its node registry (§7). The admin
  `GET /peers` leaks management detail (gossip URL, `enabled`, `apiVersion`) and is admin-only;
  this is the lean `{ nodeId, label, clientUrl, membership, status, latencyMs }` any
  authenticated user gets, enabled peers only, every membership state honestly labelled.
- **Frontend mirror:** the SPA populates its `nodes` registry from one connected
  node's converged roster — "add one, see all" for humans (§8). *(SPA-side, kgsm-web —
  not part of this backend milestone.)*
- **Self-validated (712/712 tests):** `RosterMergerTests` pins the merge decision table
  (incarnation ordering, equal-incarnation precedence, first-hand-fresh guard,
  self-refutation `+1`, disabled-not-resurrected, unknown-node insert); the in-process
  multi-node `GossipConvergenceTests` prove seed A→B + B→C converges A to know C with **no**
  direct A→C add; a genuinely-silenced node → `suspect` → `dead` → reaped; a node refutes a
  false `dead` about itself via a higher incarnation; a phantom gossiped node never reaches
  first-hand `alive`; and gossip writes **zero** `cluster_outbox` rows.
- **Follow-up owed — voluntary `left`:** the `Left` membership state exists and is
  terminal, but nothing yet *produces* it — a graceful shutdown / `cluster` opt-out
  currently goes silent and is detected as `suspect`→`dead` on the slow timer. A clean
  departure should gossip one final self-`left` so peers reap it promptly (the honest
  "I'm leaving" vs the inferred "you went silent"). Ties to open item #6. Small; fold
  into P1 or land standalone.

### P1 — Single sign-on · `built` (backend); SPA half owed
- **`POST /auth/cluster-session` vouch endpoint** — cluster-token authed +
  disable-list gated (the `/peers/inbox` fail-closed preamble). A peer asserts an
  already-authenticated identity `{ discordId, username, displayName, tier }`; this
  node mints its **own** native session (own `sid`, own registry, own sliding refresh)
  and returns `{ accessToken, refreshToken, sid, expiresAt }`. It never calls Discord —
  the vouch *is* the trust (§0). An unparseable/empty tier floors to `viewer`
  (authenticated, never escalated, never denied). Audited `auth.cluster_session`
  (actor = the vouched user, `origin: api`, vouching node in `meta.peerNode`).
- **Cluster-wide logout** — the self `POST /auth/session/revoke {all:true}` and the
  admin `.../sessions/revoke-all` enqueue a durable `session.revoke`
  (`{ scope: "user", discordId }`) to peers after the local revoke. Durable to down
  nodes (outbox redelivery on return). Peers-only — the local effect already ran
  in-process, so this node is never a target; the enqueue is **not** transactional
  with the local revoke (the documented narrow crash window, accepted — a returning
  node re-vouches). A single-`sid` self-revoke stays node-local (not cluster-wide).
- **Durable fan-out targets first-hand-`alive` peers only (locked).** The two
  transports split their target sets the same way §2·b G4 splits their retention:
  ephemeral gossip (`/peers/sync`) reaches **any** enabled roster member, but a
  **durable**, identity-carrying message (`session.revoke` names a `discordId`) fans
  out **only** to peers this node has authenticated first-hand. First-hand-alive is
  **two** conditions, not one: `MembershipState == alive` **AND** `LastSeen` set — a
  purely gossip-learned peer is *stored* `alive` with a null `LastSeen` (it displays
  as the derived `joining`), and only this node's own probe / an authenticated inbound
  contact stamps `LastSeen`. That second condition is exactly what excludes an
  unconfirmed hearsay/phantom URL from receiving a secret-bearing message (or sitting
  in the outbox retrying to a corpse for the 7-day TTL). `RosterClusterTargetProvider`
  applies the filter; ephemeral gossip is unaffected (it reads `ListEnabledAsync`
  directly, not this provider).
- **SPA-facing initiator (G2):** `POST /auth/cluster-session/request { nodeId }` — the
  **user-authed** front to the node-to-node vouch receiver (§7). The browser holds no cluster
  secret, so it cannot call `/auth/cluster-session` directly; it calls this on a node it **is**
  logged into (A), which reads the caller's asserted identity **from its own session claims**
  (`SessionClaims.ReadIdentity`/`ReadTier` — never the request body, so the tier can't be
  laundered), mints a cluster service token, relays to the target peer's receiver
  (`GossipUrl ?? Url`), and returns B's `201` verbatim. Any-tier (viewer floor — SSO preserves
  tier); a relay failure is an honest `502 peer_unreachable` (fail-closed). This is the
  server-side half of lazy vouch-on-`401`.
- **Owed (SPA, kgsm-web — not this backend milestone):** lazy vouch-on-`401` and the
  per-device session aggregation in Active Sessions.
- **Self-validated (726/726 tests, +14):** a two-node vouch (`201` + a real session
  row on the receiver + the returned token authenticates a follow-up call); vouch
  auth/validation failures (`401` missing/garbage/wrong-secret, `403` disabled peer,
  `400` missing id, tier → viewer); cross-node logout-everywhere (local revoke + bus
  delivery revokes the peer's session); the durable→alive filter (a hearsay/phantom
  peer receives **no** outbox row, plus focused `RosterClusterTargetProvider` unit
  facts for alive+LastSeen / alive+null / suspect / disabled); and down-node
  redelivery (queued while down → delivered on return).

### P2 — Resource visibility · `built`
Two distinct reads, kept separate (they must not collapse into one node-proxy):
- **The SPA reads peer resources DIRECTLY** (browser → the peer's advertised client
  URL, over its own native session) — per-node resources stay on the per-node pages
  (§8), matching the keystone's client-side rollup (no `/fleet`, no node aggregating
  peers for the browser).
- **Server-side capacity fan-out** is the only node-proxied path: `GET
  /peers/{id}/resources | /capabilities | /library` (service-token relay, reuse
  existing DTOs), consumed by the on-demand "find a node with capacity" logic (§2
  #22–#24 honesty rules) and the assistant — never by the SPA.
- **Frontend gate:** Cluster page renders local node + peers; capacity honestly
  labels `unknown` where a blueprint declares no requirement.

### P3 — Placement recommendation · `planned`
- Advisory redirect: a full node suggests a peer with headroom; the SPA opens the
  target's install form pre-filled; the target validates-at-use.

### P4 — Federated assistant · `planned`
- Optional `nodeId` on existing tools; API-side peer routing (service-token relay);
  `get_cluster_overview`.
- **Two-axis honesty carries up:** the assistant's cached peer awareness (§5 A1) is
  the converged roster, which holds non-`alive` hearsay — it may say a peer *exists*
  but must not assert a non-`alive` peer's resources or liveness as fact; a fan-out
  answer degrades to a partial (A8), never a fabricated peer state.

### P5 — Cross-node audit · `open`
- Query a peer's audit log; an "all cluster events" view.

---

## 7 · Wire contracts

### `GET /api/v1/peers` (admin-gated)
```json
{ "peers": [ {
  "id": "abc123", "url": "https://node-b:8097", "gossipUrl": "https://10.0.0.2:8097",
  "nickname": "Gaming Box", "nodeId": "node-b", "status": "reachable",
  "membership": "alive", "latencyMs": 12, "lastSeen": "2026-07-10T12:00:00Z",
  "apiVersion": "v1", "enabled": true
} ] }
```
`url` is the advertised client URL (browser-reachable); `gossipUrl` the node-to-node
address (null when equal). `status` = this node's first-hand probe; `membership` =
the gossip-converged state (alive/suspect/dead/left, or the derived `joining` for
hearsay this node has not yet authenticated first-hand).

### `GET /api/v1/peers/roster` (viewer-gated — the SPA node list, G1)
The **browser-facing** projection of the converged roster: the read that powers "add
one, see all" for a non-admin. `GET /peers` is admin-gated and carries management
detail (the gossip URL, the `enabled` flag, `apiVersion`) a viewer must not see; this
is the lean, tier-scoped roster any authenticated user reads to auto-populate its node
registry.
```json
{ "nodes": [ {
  "nodeId": "node-b", "label": "Gaming Box",
  "clientUrl": "https://node-b:8097", "membership": "alive",
  "status": "reachable", "latencyMs": 12
} ] }
```
- **Viewer-gated** (`AuthPolicy.Viewer` — the floor; any authenticated user, tier-scoped).
- **Enabled peers only.** Disable is an admin management state — a viewer neither sees a
  disabled node nor is handed a URL to reach it. Every **membership** state is otherwise
  present (`alive`/`joining`/`suspect`/`dead`/`left`), honestly labelled, so the SPA renders
  a hearsay/`joining` or `suspect` node provisionally and decides for itself whether to
  auto-add it — the API never hides a state, only the management columns.
- `clientUrl` = the **advertised** browser-reachable URL (`PeerEntity.Url`), **never** the
  node-to-node gossip URL — the browser must be able to reach it directly (§2 #13a).
- `label` = `Nickname ?? NodeId`; `membership` = the same `GossipState.Display` derivation
  the admin `PeerView` uses (yields `joining` for un-authenticated hearsay).
- **Self is not in the list** — a node is not its own peer; the SPA already holds the node it
  connected to. No `enabled`, `gossipUrl`, or `apiVersion` leaks to the viewer tier.

### `POST /api/v1/peers` (admin-gated)
```json
{ "url": "https://node-b:8097", "nickname": "Gaming Box" }
→ 201 { id, url, nickname, nodeId, apiVersion, status:"reachable", enabled:true }
→ 400 { error: { code: "invalid_url" } }
→ 409 { error: { code: "version_mismatch", details: { remote:"v2", local:"v1" } } }
→ 422 { error: { code: "peer_not_cluster",
        message: "Remote node does not advertise the cluster capability" } }
→ 502 { error: { code: "peer_unreachable" } }
```

### `GET /api/v1/peers/identity` (cluster-token authed)
```json
{ "nodeId": "node-b", "apiVersion": "v1", "build": "0.1.0+abc123",
  "capabilities": ["monitor","watchdog","cluster"] }
```

### `POST /auth/cluster-session` (cluster-token authed + disable-gated)
```json
// body: the vouched identity asserted by the calling node. `tier` is the origin's
// already-resolved tier (viewer/operator/admin), NOT a Discord-role list: the origin
// holds only its resolved tier in the session claim snapshot and peers never call
// Discord, so the target trusts the asserted tier (§0 uniform role map makes it
// consistent). An unparseable/empty tier floors to viewer.
{ "discordId": "1234", "username": "krystal", "displayName": "Krystal",
  "tier": "operator" }
→ 201 { accessToken, refreshToken, sid, expiresAt }   // B's OWN native session
→ 400 { error: { code: "bad_request" } }              // missing discordId
→ 401 { error: { code: "invalid_cluster_token" } }
→ 403 { error: { code: "peer_disabled" } }
```

### `POST /auth/cluster-session/request` (user-authed — the SPA vouch initiator, G2)
The receiver above is node-to-node (cluster-token authed) — the **browser cannot call it**
(it holds no cluster secret). This is the **user-authed** initiator the SPA calls on a node
it **is** logged into (A) to be vouched onto a node it is **not** (B): A relays to B's
receiver on the caller's behalf and returns B's tokens. It is the server-side half of lazy
vouch-on-`401`.
```json
// caller: a normal user session on THIS node (any tier — viewer floor). body:
{ "nodeId": "node-b" }
→ 201 { accessToken, refreshToken, sid, expiresAt }   // B's native session, relayed verbatim
→ 400 { error: { code: "bad_request" } }              // missing nodeId
→ 401 { error: { code: "unauthorized" } }             // no/!valid user session on this node
→ 404 { error: { code: "unknown_node" } }             // nodeId not in this node's roster
→ 403 { error: { code: "peer_disabled" } }            // the target peer is disabled here
→ 502 { error: { code: "peer_unreachable" } }         // B unreachable or refused the vouch
```
- **User-authed, any tier** (`AuthPolicy.Viewer` floor) — SSO preserves the caller's tier
  (the §0 uniform role map makes "operator on A" == "operator on B"); a viewer vouching a
  viewer session is correct, never an escalation.
- The caller's asserted identity is read **from their own validated session claims on this
  node** (`SessionClaims.ReadIdentity`/`ReadTier`) — `discordId`, `username`, `displayName`,
  and the resolved **`tier`** (as the wire string, §7 receiver). It is **never** taken from
  the request body — the body carries only `nodeId`. This is what forecloses privilege
  laundering: A asserts to B exactly the tier A itself resolved for this user, nothing the
  caller can influence.
- A looks the peer up by `nodeId` (`PeersStore.GetByNodeIdAsync`), mints a **cluster service
  token** (`IClusterTokenService.Mint`), and `POST`s the receiver at the peer's node-to-node
  address (`GossipUrl ?? Url`) reusing the cluster HTTP client. B's `201` body is relayed
  back **verbatim**; any non-`201`/unreachable is an honest `502 peer_unreachable`
  (fail-closed — this is auth, never fabricate a session on a relay failure).
- A disabled target → `403 peer_disabled`; a `nodeId` not in the roster (including on a
  non-cluster node, whose roster is empty) → `404 unknown_node`.

### `GET /api/v1/peers/self/resources` (cluster-token authed + disable-gated)
What this node exposes to a cluster peer's server-side fan-out. Cluster-token authed with the same
fail-closed preamble as `/peers/inbox`, and — unlike `/peers/identity` (token-only, so a not-yet-joined
node can still identify itself) — a resource read IS disable-gated: an explicitly-disabled peer gets
`403 peer_disabled`. A lean projection of the §4·a host capacity strip; `cpuPct`/`mem`/`disks` are honest
`null` when no metrics snapshot exists (never fabricated — the "metric-presence ≠ status" invariant).
```json
{ "id": "node-b", "label": "Gaming Box", "status": "online",
  "cpuPct": 37, "mem": { "used": 9.2, "total": 32 },
  "disks": [ { "mount": "/", "used": 180, "total": 512 } ] }
```

(`/peers/self/capabilities` and `/peers/self/library` reuse the existing
capability and `LibraryEntry` shapes verbatim, same auth + gate.)

### `GET /api/v1/peers/{id}/{resources|capabilities|library}` (admin-gated — the relay)
The **server-side node-proxy** (the one node-proxied path): mints a cluster service token and GETs peer
`{id}`'s `self/*` surface (`GossipUrl ?? Url`), returning the peer's body **verbatim**. Consumed by the
capacity fan-out / the assistant, **never the SPA** (§8). `{id}` is the roster-row id (as `/{id}/latency`).
Honest degradation, never a 500: `404` unknown id, `403 peer_disabled`, `502 peer_unreachable` (down peer
or non-2xx).

### `POST /api/v1/peers/inbox` (cluster-token authed)
The message-bus receive endpoint — one endpoint, typed envelope. Full contract:
`docs/cluster-message-bus-plan.md`.

---

## 8 · SPA changes

> **The SPA-side authority is `kgsm-web/docs/cluster-plan.md`** — the phased browser plan
> (SPA-C0…C5, aligned to these phases), with the real file seams, the two SPA-facing API
> dependencies it surfaces (a viewer-readable node list; a user-authed vouch *initiator* —
> neither built yet), and the honest baseline (the SPA is single-host for auth today; SSO is
> what unblocks N≥2). The bullets below are the API-side summary; follow that doc for the build.

- Rename the `localStorage` connection registry `hosts → nodes` (same structure;
  provide a one-time in-place migration so existing connections survive the
  rename).
- **Cluster discovery = the browser-side mirror of backend gossip.** Once the SPA
  connects+auths to **one** node, it pulls that node's converged roster and
  auto-populates the `nodes` registry with the whole cluster (using each entry's
  **advertised client URL**, §2 #13a) — "add one, see all" for humans. A new user
  who logs into any node is shown the admin-built cluster already assembled, scoped
  to their tier; no per-node setup. This depends on the roster carrying
  browser-reachable URLs + each peer allowing the SPA origin via CORS (the
  preflight-probe warning below).
- **Fleet page → Cluster page** (new route, replaces the old):
  - Lists the local node + all peers; add/remove/enable/disable; latency;
    reachable/unreachable/disabled status.
  - **No resource dashboard** — per-node resources stay on the per-node pages.
- **Single sign-on:** one Discord login; the SPA lazily vouches a native session
  on each node as it is first touched (401 → vouch → retry). Active Sessions
  aggregates per device across the cluster; "Sign out everywhere" fans out over the
  bus.
- **Cross-node install:** the install modal's node dropdown is populated from the
  cached Cluster-page data. Selecting a peer switches the SPA to talk **directly**
  to that peer (it already holds, or lazily vouches, a native session there); the
  source node drops out of the loop.
- **CORS is a setup requirement:** for the browser to call a peer directly, that
  peer's `KGSM_API_CORS_ORIGINS` must list the SPA origin. The Cluster page runs a
  browser-side preflight probe per peer and **warns** on a CORS/reachability
  mismatch rather than failing opaquely mid-install.

---

## 9 · Self-validation plan

### P0
- Add a peer (reachable → stored; unreachable → `502`; non-cluster → `422`;
  version mismatch → `409`).
- Mint a service token, verify it passes; verify a `previous`-secret token during a
  simulated rotation window; reject a disabled peer (`403`).
- Disable-list gate: an unknown validly-tokened node is accepted; a disabled one is
  rejected (`403`).

### P0.5
- Seed A→B and B→C; A converges to know C with **no** direct A→C add.
- Kill a node → `suspect` → `dead` → reaped; the killed node refutes a false `dead`
  on return via a higher incarnation.
- A phantom node injected by gossip never reaches `alive` (fails first-hand auth).
- Gossip traffic leaves no `cluster_outbox` rows (ephemeral transport).

### P1
- Log into A; hit B with no B session → transparent vouch → native B session.
- Logout-everywhere revokes A **and** B.
- Down-node revocation: stop B, logout-everywhere on A, restart B → the queued
  `session.revoke` is delivered on B's return (bus redelivery), session gone.

### P2
- Query a peer's resources/capabilities/library.
- Fan-out "find capacity": honest `unknown` for an undeclared blueprint; a correct
  pick for a declared one.
- Cluster page renders peer data; a down peer degrades to honest "unreachable," not
  a 500.

---

## 10 · Open items

Resolved by earlier rounds and no longer open: handshake model, node auth, trust
direction, version policy, SSO mechanism, logout durability, capacity honesty,
fail-open/closed split, Ed25519 (dropped).

Still open — to resolve before or during the relevant phase:

1. **Clock skew — narrowed to token `exp`.** Membership is skew-immune by
   construction: convergence orders by **incarnation integers**, and the failure
   detector times each node's evidence on its **own** clock (§2·b G5 — no cross-node
   wall-clock compare). The only skew-sensitive surface left is the service-token
   `exp` (and any timestamped bus envelope). Decide the tolerance and the honest
   fail-closed error when a node's clock is badly off (it must reject, not silently
   accept).
2. **Topology: LAN vs WAN, and the two-URL split.** `GET /peers/identity` is
   reachable **before** any trust is established (you can't authenticate before you
   know the peer). If nodes span the public internet, that endpoint is an
   internet-exposed enumeration/DoS surface — decide LAN-only (VPN/overlay) vs WAN,
   and rate-limit `/identity` + `/inbox` + `/sync` accordingly. Ties into who opens
   the cross-node port (kgsm-firewall/watchdog own port-opening). **Concrete
   sub-decision (§2 #13a):** the two-URL split is **built as config knobs** —
   `ClusterAdvertiseUrl` (the browser-reachable client URL the roster carries) and
   `ClusterGossipUrl` (the node-to-node address). What remains open is
   **auto-derivation** (so an operator need not set both by hand) and the **SPA CORS
   preflight-warn** (§8) — so the roster the SPA consumes is always browser-reachable,
   never an internal address.
7. **Role-map drift honesty.** §0 makes a uniform `KGSM_API_AUTH_ROLE_*` map a
   cluster-formation precondition, so tier is consistent by construction. If an
   operator lets two nodes' maps drift, a user is legitimately admin on one and
   viewer on another — not a bug, an honest per-node result. Now that gossip exists,
   the concrete seam is cheap: piggyback a **role-map hash** on the `/peers/sync`
   payload and **warn** on mismatch (a diagnostic surfaced on the Cluster page), never
   reconcile. Decide whether to build that check or leave it to operator discipline;
   do **not** silently reconcile either way.
3. **TLS cert validation.** Over the shared-secret model, is TLS validated
   (proper certs / internal CA) or is it encryption-only with the service token as
   the sole identity layer? Pin one; if self-signed, document the accepted risk.
4. **Placement soft-reservation.** #24 accepts the TOCTOU race for MVP; revisit if
   double-booking bites in practice (a short-lived reservation on the target).
5. **Nickname divergence.** Nicknames are node-local; the SPA aggregates — decide
   which label wins when two nodes name the same peer differently (cosmetic).
6. **`peer_disabled` after cluster opt-out.** A node that drops the `cluster`
   capability while peers still list it: its `/peers/self/*` and `/inbox` must
   `404`/`403` cleanly, and peers must reflect it as unavailable, not fabricate a
   status.
