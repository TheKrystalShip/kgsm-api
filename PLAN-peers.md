# KGSM Cluster — Peer Federation (Constellation)

> Living document. Design + locked decisions + phased delivery for
> **node-to-node peer federation** — the mesh that lets independently-deployed
> `kgsm-api` nodes discover each other's resources, recommend placement, and
> federate the assistant across a cluster.
>
> Extends the O7 stub in `system-architecture.md §5`; companion to
> `kgsm-api/PLAN.md` (the per-host milestone plan).

---

## Status legend
`built` = exists & verified · `partial` = exists, incomplete · `planned` =
designed, not built · `open` = not yet decided.

---

## Terminology

| Old | New | Scope |
|---|---|---|
| Fleet | **Cluster** | SPA page, all docs, all code |
| Host | **Node** | Everywhere a kgsm-api deployment is referenced |
| Host capabilities | **Node capabilities** | §4·b model |
| `localStorage` host registry | `localStorage` **nodes** registry | SPA persistence |
| Fleet page | **Cluster page** (replaces, new route) | SPA |

**Node** = a kgsm-api deployment with the `cluster` capability explicitly
provisioned. A kgsm-api that does not advertise `cluster` is **invisible** to
the mesh — not queryable, not listed, not discoverable. The `cluster` capability
is a **gate only** — it does not carry its own status line on the Cluster page;
participation is binary (in or out), and node health is derived from latency +
per-leaf status.

---

## 1 · Design decisions (all locked)

| # | Decision | Choice |
|---|---|---|
| 1 | Renaming | Fleet → Cluster; host → node; ecosystem-wide |
| 2 | Node definition | kgsm-api with `cluster` capability provisioned |
| 3 | Non-cluster APIs | Invisible — not queryable, not listed |
| 4 | Peer transport | REST over HTTPS on existing API surface |
| 5 | Node identity | Config-driven id (same as HostId) + Ed25519 public key (separate field) |
| 6 | Trust model | TOFU — first connection trusted if admin confirms |
| 7 | Auth layers | Node keypair (channel) + user JWT (authorization, re-verified) |
| 8 | Request signing | `Ed25519(<timestamp>\|<method>\|<path>\|<bodyHash>)` — prevents replay + body tampering |
| 9 | Discovery | Admin introduces (paste URL) |
| 10 | Peer list storage | SQLite (same DB, `Peers` table) |
| 11 | Keypair storage | SQLite blob column |
| 12 | Keypair generation | First startup (always, even if cluster disabled) |
| 13 | Data shapes | Reuse existing Host/Server/Library DTOs |
| 14 | State sharing | On-demand fan-out; validate-at-use |
| 15 | Version policy | Strict match enforced at handshake |
| 16 | Failure mode | Fail-open (report and continue) |
| 17 | Compromised key | Node disabled (not deleted); admin manually re-trusts |
| 18 | Library | Per-node, can differ — fan-out needed |
| 19 | Peer health | Latency endpoint, 10s interval |
| 20 | Leaf health per peer | SSE to SPA (already handled per-node) |
| 21 | Replay window | Configurable via env var (`KGSM_API_PEER_REPLAY_WINDOW_S`), default ±30s |
| 22 | URL validation at add | Validate reachability before storing (fail-fast) |
| 23 | Cluster page | Replaces Fleet page; peer management only (add/remove/enable/disable, latency) |
| 24 | SPA node registry | Rename existing `localStorage` "hosts" → "nodes" |
| 25 | Install modal peer data | Cached from Cluster page |
| 26 | SPA install flow | Modal updates host dropdown → SPA talks directly to target node → source node out of loop |
| 27 | Cluster page route | New route replaces old Fleet route; breaking change acceptable |
| 28 | Cross-node install | SPA updates host dropdown, talks directly to target node |
| 29 | SPA host switching | Already supported (multi-host client) |

---

## 2 · The peer protocol

### 2.1 Node handshake

```
Admin: POST /api/v1/peers { url: "https://node-b:8097", nickname: "Gaming Box" }
  → node A calls GET {peer}/api/v1/peers/identity
  → node B returns { nodeId, publicKey, apiVersion, build }
  → node A checks: apiVersion matches (strict) → trustStatus = "trusted"
  → node A checks: node B advertises cluster capability
  → stores (url, nickname, nodeId, publicKey, trustStatus, apiVersion, build)
  → node A can now query node B's peer endpoints
```

### 2.2 Endpoint surface

**Peer management (admin-gated, local node):**
- `GET /api/v1/peers` — list known peers
- `POST /api/v1/peers` — add a peer → handshake + trust
- `DELETE /api/v1/peers/{id}` — remove a peer
- `PATCH /api/v1/peers/{id}` — update nickname, disable/enable
- `GET /api/v1/peers/{id}/latency` — measure round-trip

**Node-to-node (cluster-authenticated, signed):**
- `GET /api/v1/peers/identity` — this node's identity
- `GET /api/v1/peers/identity/verify` — verify a remote node's key
- `GET /api/v1/peers/self/resources` — CPU/RAM/Disk (host DTO shape)
- `GET /api/v1/peers/self/capabilities` — capability set
- `GET /api/v1/peers/self/library` — game catalog (library DTO shape)
- `GET /api/v1/peers/self/servers` — running servers (server DTO shape)

### 2.3 Request signing format

```
Headers on every node-to-node request:
  X-Node-Id: <requesting node's id>
  X-Timestamp: <unix timestamp, seconds>
  X-Signature: <base64 Ed25519 signature>

Signature payload (UTF-8 bytes):
  <timestamp>|<HTTP method>|<path>|<SHA256 of request body, or empty string for GET>

Verification:
  1. Look up publicKey for X-Node-Id in trust store
  2. Check X-Timestamp is within ±30s of server time (configurable)
  3. Verify Ed25519 signature against the payload
  4. Reject if any step fails
```

### 2.4 On-demand fan-out with validate-at-use

```
User selects "Install factorio" on node A
  → node A checks own resources (GET /hosts/primary)
  → If insufficient: fan out GET /peers/{id}/resources to all enabled peers
  → Find peers with headroom → sort by most free
  → Propose: "Node B has capacity — install there?"
  → User clicks "Install on Node B"
  → SPA opens node B's install page (pre-filled with blueprint: factorio)
  → node B validates resources at install time (validate-at-use)
  → If still available → proceed. If not → honest error.
```

---

## 3 · Trust & auth model

```
┌─────────────────────────────────────────────────────────┐
│  Node A                                                  │
│  Ed25519 keypair (generated at first startup)            │
│  Config-driven nodeId (default: hostname)                │
│  Trust store: { nodeB: trusted, nodeC: disabled }        │
└───────────────────┬─────────────────────────────────────┘
                    │
                    │  HTTPS to node B:
                    │  X-Node-Id: A
                    │  X-Timestamp: 1719000000
                    │  X-Signature: Ed25519("1719000000|GET|/api/v1/peers/self/resources|")
                    │  Authorization: Bearer <user's JWT>
                    │
                    ▼
┌─────────────────────────────────────────────────────────┐
│  Node B                                                  │
│  1. Look up node A's publicKey from trust store          │
│  2. Verify X-Signature (Ed25519)                         │
│  3. Check X-Timestamp freshness (±30s)                   │
│  4. Verify user JWT (same Discord app, node B's key)     │
│  5. Check user tier for the operation                    │
│  6. Execute + return result                              │
└─────────────────────────────────────────────────────────┘
```

- **Node keypair** proves the request is from a known peer
- **User JWT** is from node A; node B **re-verifies** it (same Discord application, same signing key)
- **Timestamp** prevents replay attacks (configurable window)
- **Disabled node** → requests rejected with 403; node stays in DB for re-trust

---

## 4 · Peer health (latency)

- Node A periodically calls `GET /peers/{id}/latency` every **10s**
- Measures round-trip time, stores in `latencyMs`
- If no response within timeout → mark `status: "unreachable"`
- Cluster page reads `GET /peers` which includes `latencyMs` + `status`
- Individual leaf health per peer is pushed via the existing SSE stream (each
  node's `capabilities` channel carries its own leaf health — no new SSE topic)

---

## 5 · Assistant federation (P3)

### Design decisions

| # | Decision | Choice |
|---|---|---|
| A1 | Assistant awareness | Always aware of peers (via cached state), acts only on local failure |
| A2 | Cross-node execution | Recommend only — user confirms on target node |
| A3 | Cross-node memory | On-demand queries only — no persistent cross-node context |
| A4 | Peer tools | Extend existing tools with optional `nodeId` parameter |
| A5 | Tool call format | `nodeId` as tool parameter |
| A6 | Auth for peer queries | Relay-auth (same as M7) — local API proxies, handles node signing |
| A7 | Wire path | Assistant → local API → proxy to peer → result back |
| A8 | Peer data freshness | Real-time query (always live) |
| A9 | Recommendation UX | Triggers install modal on target node (pre-filled) |
| A10 | Peer query failure | Single retry, then honest failure |

### Tool extension

The assistant's existing tools gain an optional `nodeId` parameter:

```
// Before (local only):
get_servers() → [{ id: "factorio", status: "running", ... }]

// After (with nodeId):
get_servers() → [{ id: "factorio", status: "running", ... }]
get_servers(nodeId: "node-b") → [{ id: "terraria", status: "stopped", ... }]
```

Affected tools (at minimum):
- `get_servers` — query running servers on a node
- `get_library` — query available blueprints on a node
- `get_host_status` — query resources/capabilities on a node

New tool (or extension):
- `get_cluster_overview` — aggregate resources across all nodes (fan-out)

### Wire path (relay through local API)

```
User: "What games can I install across my cluster?"

Assistant calls: get_library() [no nodeId — local]
  → API resolves locally → returns local library

Assistant calls: get_library(nodeId: "node-b") [peer query]
  → API sees nodeId parameter
  → API routes to: GET /api/v1/peers/self/library on node B
    (signed with node A's keypair, user JWT re-verified at node B)
  → node B returns its library
  → API returns to assistant
  → Assistant aggregates and responds to user
```

### Failure handling

```
Assistant calls: get_servers(nodeId: "node-b")
  → node B unreachable
  → API retries once (after 2s)
  → Still unreachable
  → API returns: { error: { code: "peer_unreachable" } }
  → Assistant tells user: "Node B is currently unreachable."
```

### Implementation scope

- **API side:** extend tool schemas with `nodeId`; add peer-routing logic; handle
  peer query failures; expose `get_cluster_overview` tool
- **Assistant side (kgsm-llm):** tool definitions gain `nodeId` optional parameter;
  no new implementations — routing is API-side
- **SPA side:** handle assistant trigger for cross-node install modal; existing
  modal flow handles the rest

---

## 6 · Phased delivery

### P0 — Peer foundation (protocol + trust) · `planned`

- Ed25519 keypair generation at startup, stored in SQLite
- `Peers` table (id, url, nickname, nodeId, publicKey, trustStatus, lastSeen,
  latencyMs, apiVersion, build, enabled)
- `NodeIdentity` table or column (nodeId, publicKey, privateKey)
- `cluster` capability added to `LeafCatalog`
- `PeersController` (CRUD + identity endpoint)
- Request signing middleware + Ed25519 verification
- Version check at handshake (strict match)
- Disabled-peer rejection (403)
- Latency measurement (`GET /peers/{id}/latency`)
- Latency background poller (10s interval, stores in DB)
- **Self-validated:** smoke test adds peer, signs request, verifies identity,
  rejects version mismatch, rejects disabled peer, rejects stale timestamp

### P1 — Resource visibility (MVP) · `planned`

- `GET /peers/{id}/resources` (reuses host DTO shape)
- `GET /peers/{id}/capabilities` (reuses capability model)
- `GET /peers/{id}/library` (reuses library DTO shape)
- On-demand fan-out: "find node with capacity" logic
- **Frontend gate:** Cluster page renders peer nodes alongside local node;
  capacity strip shows all nodes

### P2 — Placement recommendation · `planned`

- Advisory: when install is attempted on a full node, recommend a peer with headroom
- SPA shows "Node B has free resources — install on Node B?" redirect
- Pre-filled redirect: SPA opens target node's install form with blueprint pre-selected
- Validate-at-use: target node re-checks resources before executing

### P3 — Federated assistant · `planned`

- Extend existing tools with optional `nodeId` parameter
- Peer routing logic in API (proxy to peer endpoints)
- Assistant-to-assistant communication (if peer has `assistant` capability)
- Direct API fallback (if peer has no assistant)
- `/peers/{id}/assistant/turn` relay endpoint
- `get_cluster_overview` tool (fan-out across all nodes)

### P4 — Cross-node audit (optional, future) · `open`

- Cross-node audit trail visibility (query a peer's audit log)
- "All cluster events" view

---

## 7 · Wire contracts

### `GET /api/v1/peers` (admin-gated)

```json
{
  "peers": [
    {
      "id": "abc123",
      "url": "https://node-b:8097",
      "nickname": "Gaming Box",
      "nodeId": "node-b",
      "status": "reachable",
      "latencyMs": 12,
      "lastSeen": "2026-07-06T12:00:00Z",
      "apiVersion": "v1",
      "build": "0.1.0+abc123",
      "enabled": true
    }
  ]
}
```

### `POST /api/v1/peers` (admin-gated)

```json
{ "url": "https://node-b:8097", "nickname": "Gaming Box" }
→ 201 { id, url, nickname, nodeId, status: "trusted", ... }
→ 400 { error: { code: "invalid_url" } }
→ 409 { error: { code: "version_mismatch", details: { remote: "0.5.0", local: "0.1.0" } } }
→ 422 { error: { code: "peer_not_cluster", message: "Remote node does not advertise cluster capability" } }
```

### `GET /api/v1/peers/identity` (cluster-authenticated)

```json
{
  "nodeId": "node-b",
  "publicKey": "<base64 Ed25519 public key>",
  "apiVersion": "v1",
  "build": "0.1.0+abc123"
}
```

### `GET /api/v1/peers/self/resources` (cluster-authenticated)

```json
{
  "id": "node-b",
  "label": "Gaming Box",
  "status": "online",
  "cpuPct": 37,
  "mem": { "used": 9.2, "total": 32 },
  "disks": [{ "mount": "/", "used": 180, "total": 512 }]
}
```

### `GET /api/v1/peers/self/capabilities` (cluster-authenticated)

```json
{
  "monitor": { "provisioned": true, "status": "operational" },
  "watchdog": { "provisioned": true, "status": "operational" },
  "assistant": { "provisioned": false, "status": "absent" },
  "cluster": { "provisioned": true, "status": "operational" }
}
```

### `GET /api/v1/peers/self/library` (cluster-authenticated)

```json
{
  "entries": [
    { "id": "factorio", "name": "Factorio", "type": "native", "steamAppId": 427520 }
  ]
}
```

---

## 8 · SPA changes

- Rename `localStorage` host registry → "nodes"
- Fleet page → **Cluster page** (new route, replaces old):
  - Lists local node + all peers
  - Add/remove/enable/disable peers
  - Shows latency per peer
  - Peer status (reachable/unreachable)
  - **No resource dashboard** — that stays on the per-node pages
- Install modal:
  - Host dropdown populated from cached node data (local + peers from Cluster page)
  - When selected node lacks capacity: suggest peer with headroom
  - User agrees → dropdown switches to peer → SPA talks directly to peer
  - Source node is out of the loop from that point

---

## 9 · Self-validation plan

### P0 smoke

- Add a peer (validate-at-add: reachable → stored; unreachable → rejected)
- Reject version mismatch
- Reject non-cluster peer
- Sign a request, verify it passes
- Reject a disabled peer
- Reject a stale timestamp (outside replay window)

### P1 smoke

- Query peer resources (real peer or stub)
- Query peer capabilities
- Query peer library
- Fan-out: find node with capacity when local node is full
- Cluster page renders peer data

---

## 10 · Open items (implementation-level)

These resolve naturally during build — they don't need upfront design:

1. **Keypair storage** — SQLite blob column in a `NodeIdentity` table or inline
   in `Peers`.
2. **Key generation timing** — first startup (always). Key exists even if
   cluster is disabled.
3. **SPA redirect model** — inline modal: SPA updates host dropdown, talks
   directly to target node, source node out of loop.
4. **URL validation** — validate at add time (fail-fast on bad URL).
5. **Latency interval** — 10s default, configurable via env var.
6. **Replay window** — ±30s default, configurable via
   `KGSM_API_PEER_REPLAY_WINDOW_S`.
7. **Body hash** — GET: empty string after final `|`. POST: SHA256(body).
8. **Cluster capability placement** — new entry in `LeafCatalog`, same
   `provisioned`/`status` model as existing capabilities.
9. **SPA localStorage rename** — rename host registry key to "nodes"; same data
   structure, new name.
10. **Disabled peer behavior** — no latency ping, shown as disabled on Cluster
    page, requests rejected with 403.
11. **Cross-node install modal** — SPA stays on source node, updates host
    dropdown with target, then talks directly to target node's API.
