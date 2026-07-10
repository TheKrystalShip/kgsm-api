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
| 7 | Trust direction | **Symmetric by construction** — a node trusts any caller bearing a valid cluster-secret service token whose `iss` is an enabled peer |
| 8 | Node disable | A row flag in the `Peers` table — disabled ⇒ its service tokens rejected (`403`); stays in DB for re-enable |
| 9 | Secret rotation | Dual-secret overlap window: nodes accept `{current, previous}`, roll one at a time, drop `previous` |
| 10 | Version policy | **Match on `apiVersion` (`v1`)**, not build version — allows rolling upgrades across a heterogeneous-build mesh |
| 11 | Discovery | Admin introduces (paste URL); no gossip/auto-discovery (cluster is small, hand-formed) |
| 12 | Peer + secret storage | SQLite (`Peers` table; secret from config/env, never stored) |

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

- Each node polls every **enabled** peer's `GET /peers/{id}/latency` on a **10s**
  interval, stores `latencyMs` + `status`.
- No response within timeout ⇒ `status: "unreachable"`. Disabled ⇒ no ping,
  `status: "disabled"`.
- The Cluster page reads `GET /peers` (includes `latencyMs` + `status`). Per-leaf
  health of each peer rides the SPA's existing per-node `capabilities` SSE — no new
  SSE topic.
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
- `KGSM_API_CLUSTER_SECRET` config + the service-token mint/verify seam
  (current + previous secret during rotation).
- `Peers` table (id, url, nickname, nodeId, status, latencyMs, lastSeen,
  apiVersion, enabled).
- `cluster` capability in `LeafCatalog`.
- `PeersController`: CRUD + `GET /peers/identity` + `GET /peers/{id}/latency`.
- Handshake (paste URL → pull identity → `apiVersion` match → store); reachability
  validated at add time; disabled-peer rejection (`403`).
- Latency poller (10s), feeding bus liveness.
- **Self-validated:** add a peer, mint+verify a service token, reject a version
  mismatch, reject a non-cluster peer, reject a disabled peer.

### P1 — Single sign-on · `planned`
- `POST /auth/cluster-session` vouch endpoint (service-token authed → native
  session mint).
- SPA lazy vouch-on-401 + per-device session aggregation in Active Sessions.
- Cluster-wide logout: `session.revoke` over the bus (durable to down nodes).
- **Self-validated:** log into A, hit B without a B session → transparent vouch →
  B session minted; logout-everywhere revokes A and B; a down node is revoked on
  return (bus redelivery).

### P2 — Resource visibility · `planned`
- `GET /peers/{id}/resources | /capabilities | /library` (reuse existing DTOs).
- On-demand "find a node with capacity" fan-out (§2 #22–#24 honesty rules).
- **Frontend gate:** Cluster page renders local node + peers; capacity honestly
  labels `unknown` where a blueprint declares no requirement.

### P3 — Placement recommendation · `planned`
- Advisory redirect: a full node suggests a peer with headroom; the SPA opens the
  target's install form pre-filled; the target validates-at-use.

### P4 — Federated assistant · `planned`
- Optional `nodeId` on existing tools; API-side peer routing (service-token relay);
  `get_cluster_overview`.

### P5 — Cross-node audit · `open`
- Query a peer's audit log; an "all cluster events" view.

---

## 7 · Wire contracts

### `GET /api/v1/peers` (admin-gated)
```json
{ "peers": [ {
  "id": "abc123", "url": "https://node-b:8097", "nickname": "Gaming Box",
  "nodeId": "node-b", "status": "reachable", "latencyMs": 12,
  "lastSeen": "2026-07-10T12:00:00Z", "apiVersion": "v1", "enabled": true
} ] }
```

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

### `POST /auth/cluster-session` (cluster-token authed)
```json
// body: the vouched identity asserted by the calling node
{ "discordId": "1234", "username": "krystal", "displayName": "Krystal",
  "roles": ["operator"] }
→ 201 { accessToken, refreshToken, sid, expiresAt }   // B's OWN native session
→ 401 { error: { code: "invalid_cluster_token" } }
→ 403 { error: { code: "peer_disabled" } }
```

### `GET /api/v1/peers/self/resources` (cluster-token authed)
```json
{ "id": "node-b", "label": "Gaming Box", "status": "online",
  "cpuPct": 37, "mem": { "used": 9.2, "total": 32 },
  "disks": [ { "mount": "/", "used": 180, "total": 512 } ] }
```

(`/peers/self/capabilities` and `/peers/self/library` reuse the existing
capability and `LibraryEntry` shapes verbatim.)

### `POST /api/v1/peers/inbox` (cluster-token authed)
The message-bus receive endpoint — one endpoint, typed envelope. Full contract:
`docs/cluster-message-bus-plan.md`.

---

## 8 · SPA changes

- Rename the `localStorage` connection registry `hosts → nodes` (same structure;
  provide a one-time in-place migration so existing connections survive the
  rename).
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

1. **Clock skew.** Service-token `exp` and any timestamped envelope assume rough
   NTP sync across nodes. Decide the tolerance and the honest error when a node's
   clock is badly off (it must fail-closed, not silently accept).
2. **Topology: LAN vs WAN.** `GET /peers/identity` is reachable **before** any
   trust is established (you can't authenticate before you know the peer). If nodes
   span the public internet, that endpoint is an internet-exposed enumeration/DoS
   surface — decide LAN-only (VPN/overlay) vs WAN, and rate-limit `/identity` +
   `/inbox` accordingly. Ties into who opens the cross-node port
   (kgsm-firewall/watchdog own port-opening).
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
