# KGSM Cluster — Peer Federation (Constellation)

> Living design doc. The mesh that lets independently-deployed `kgsm-api` nodes,
> all belonging to **one cluster**, discover each other's resources,
> recommend placement, share a single sign-on, and federate the assistant.
>
> Extends the O7 stub in `system-architecture.md §5`; companion to
> `kgsm-api/PLAN.md` (the per-host milestone plan). The node-to-node transport it
> rides on has its own authority: **the cluster package's `docs/cluster-message-bus.md`** (built
> first — it is the foundation every peer feature depends on).

---

## Status legend
`built` = exists & verified · `partial` = exists, incomplete · `planned` =
designed, not built · `open` = not yet decided.

---

## 0 · The security boundary (read this first)

**A cluster is one trust domain, single-owner.** Every member holds the **same**
cluster secret, which proves membership between members, and accepts the sessions
of the **one** auth anchor the cluster has. Who a person is and what they may do
are the anchor's: it holds the accounts, permissions, roles and assignments, and
every member evaluates a caller against its own read-only replica of them
(`../kgsm-docs/systems/authorization/`). What follows from that:

- Authorization is coherent across the cluster by construction — a role assigned
  at cluster scope means the same thing on every member, and a narrower one
  (`node:<id>`, an instance) is evaluated identically wherever it is asked.
- Single sign-on needs nothing between nodes — a session the anchor minted is
  valid on every member, because each verifies it against the key the anchor
  publishes and none can mint one.
- **The cost, accepted eyes-open:** the members are not security-independent. A
  fully-compromised member holds the cluster secret and can speak as a member to
  every other one; it cannot forge a session. Treat the cluster's blast radius as
  that of its weakest member. This is acceptable **only** because a cluster is
  single-owner by definition. A multi-owner mesh is out of scope and would require
  per-node asymmetric identity (see §3, mTLS upgrade path).

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
| 3 | Cluster membership proof | A shared **`Cluster__Secret`** (HMAC), host-level and unrelated to the auth anchor's session signing key |
| 4 | Node-to-node auth | A **service JWT** signed with the cluster secret (`sub=node:<id>`, `aud=cluster`, `iss=<id>`, short TTL) |
| 5 | No per-node keypairs | Symmetric shared secret only — no Ed25519, no per-request asymmetric signing |
| 6 | Handshake | Somebody holding `api:members.manage` pastes a URL → the two nodes exchange **node cards** in one symmetric `POST /members/introduce` over TLS (§7), each validating the other with the same predicate and each recording the mirror of what the other records. No key exchange, no fingerprint confirmation |
| 7 | Trust direction | **Symmetric by construction** — the shared secret *is* the trust boundary. A node trusts any caller bearing a valid cluster-secret service token whose `iss` is **not an explicitly-disabled peer** (a **disable-list** gate, not an allow-list): an unknown-but-validly-tokened node is trusted, because holding the secret already proves cluster membership. This is what makes trust transitive without pairwise handshakes and lets the mesh work under a partial topology view |
| 8 | Node disable | A row flag in the `Peers` table — disabled ⇒ its service tokens rejected (`403 peer_disabled`); stays in DB for re-enable. Disable is the **only** local override to the shared-secret trust; absence from the table is *not* rejection |
| 9 | Secret rotation | Dual-secret overlap window: nodes accept `{current, previous}`, roll one at a time, drop `previous` |
| 10 | Version policy | **Match on `apiVersion` (`v1`)**, not build version — allows rolling upgrades across a heterogeneous-build mesh |
| 11 | Discovery | **Join-via-one-seed + gossip convergence.** The only membership action, ever, is "join the cluster": paste **one** existing member's URL. From that seed the roster converges automatically (§2·b) — add one, join all. No per-peer approvals, no master node |
| 12 | Peer + secret storage | SQLite (`Peers` table = the durable roster + seed set; secret from config/env, never stored) |
| 13a | Node addresses | Each node carries a **candidate list** of addresses it answers at, most-trusted first. Reachability is a property of a pair, not of a node: every peer probes the candidates and pins the one that answers *for it*, so two peers legitimately hold different addresses for the same third node |
| 13b | Where candidates come from | **Reflection, not configuration.** A node learns its own addresses from whoever demonstrably reached it — the URL a peer introduced it at. A configured public address and gossip address (an address only nodes use) are overrides for topologies where reflection is not right, and the name the cluster assigns a member leads what reflection learned; a node needs none of them to join |
| 13c | An address is a claim until probed | A reflected or gossiped address is recorded **unverified** and promoted only when a probe answers with the expected `nodeId` (the address form of G3). A node whose every candidate fails is honestly `unreachable`; no address is guessed, and none is fabricated to fill the column |

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
| G4 | Transport split | Gossip rides a **separate ephemeral, best-effort** path (`POST /api/v1/members/sync`, cluster-token authed, fire-and-forget, **no** outbox row) — never the durable outbox (which is 7-day-retained for guaranteed messages like `session.revoke`; durably retrying a stale ping to a corpse is exactly wrong). The durable bus takes its fan-out target list *from* the converged roster |
| G5 | Failure detection | A **last-evidence clock**: each node times its own evidence for each peer (no cross-node wall-clock). Evidence is **mutual** — our own successful probe OR an authenticated inbound sync FROM the peer — so a node we can't reach but that still gossips to us stays `alive` (an asymmetric partition resolves for the demonstrably-live node; no refute/re-suspect oscillation). No evidence for `ClusterSuspectMs` → `suspect`, another window silent → `dead`, reaped after `ClusterReapMs`. SWIM **indirect probe** (`ping-req` via k members) is a further refinement, deferred until flapping shows up |
| G6 | Scale ceiling (honest) | Full-roster push-pull is O(n) bytes/sync (~20 KB at 100 nodes — fine into the low thousands). Past that, move to delta/Merkle anti-entropy. 100 is inside the simple version's comfort zone; build simple, note the seam |

### Single sign-on across the cluster

| # | Decision | Choice |
|---|---|---|
| 13 | One sign-in, whole cluster | A person signs in **once**, at the auth anchor; the session it mints is accepted by every member, and what it may do is evaluated per action on each (`hosted-sign-in-plan.md`) |
| 14 | No session lives on a node | A member verifies a session against the key the anchor publishes and keeps no row for it — only the list of sessions that have ended |
| 17 | SPA connection model | A **direct multi-host client** — one session, per-node SSE, browser fans out |
| 19 | Logout everywhere | An ended session is announced over the message bus (`session.revoke`) — durable, so a node that is down when it ends refuses it when it returns |

### Placement & failure

| # | Decision | Choice |
|---|---|---|
| 20 | State sharing | On-demand fan-out; validate-at-use; no cross-node state store |
| 21 | Data shapes | Reuse existing Host/Server/Library DTOs |
| 22 | Capacity inputs | Blueprint-declared RAM/disk (where present) + the target node's live free disk/RAM + current CPU saturation |
| 23 | Capacity honesty | Undeclared requirement ⇒ **"unknown fit," never a guess.** Placement = `free disk ≥ declared AND free RAM ≥ declared`, else `unknown`. CPU is a coarse "is the node already saturated" gate, not a fit prediction |
| 24 | Placement race (TOCTOU) | **Accept the race + honest failure** at start time (MVP). Soft reservations deferred |
| 25 | Availability failures | **Fail-open**: an unreachable/slow/silent peer degrades to an honest "unknown," never a 500 |
| 26 | Auth/authz failures | **Fail-closed**: an invalid service token, a refused action, an unverifiable identity ⇒ reject (`4xx`). Distinct code path from #25 — never conflate the two |

---

## 3 · Trust & auth model

```
Cluster secret       Cluster__Secret  (HMAC, shared by every member — proves membership)
Session signing key  held by the auth anchor alone (ES256); members hold its public half
                     └─ leaking the cluster secret hands over no session forgery: a member
                        verifies a session it cannot mint.
```

### Node-to-node call

```
┌── Node A ──────────────────────────────────────────┐
│  Wants: GET {B}/api/v1/members/self/resources          │
│  Mints a service JWT:                                 │
│    { sub:"node:A", iss:"A", aud:"cluster", exp:+60s } │
│    signed with Cluster__Secret                   │
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
message-bus dedupe id (the cluster package's `docs/cluster-message-bus.md`) provides replay safety
where it matters.

### A person's session

```
Person → auth anchor: sign in (the anchor is the cluster's OpenID Connect provider)
  The anchor mints one session, audienced to the cluster, signed with its own key.

The SPA calls any member with that bearer:
  The member verifies it against the anchor's published key, refuses it if the
  ended-session list names its sid, and evaluates the request's action against
  its replica of the cluster's authority.
```

- No member talks to an identity provider and no member mints a session.
- Ending a session is the anchor's; it reaches every member as a durable
  `session.revoke` (§6, P1).

### Compromise & rotation

- **Disabled node:** its service tokens are rejected (`403`); it stays in the
  `Peers` table for one-click re-enable.
- **Stolen cluster secret:** rotate `Cluster__Secret` across all members via
  the dual-secret overlap window (#9). The `Peers.enabled` gate is an operational
  on/off, **not** a cryptographic boundary against a stolen secret (a thief can set
  `iss` to any enabled peer) — rotation is the real remedy.
- **Upgrade path (documented, not built):** if a multi-owner or large mesh ever
  becomes a requirement, replace the shared secret with **mTLS + a shared CA** —
  per-node certs give granular CRL revocation without a cluster-wide rotation. Out
  of scope while clusters are single-owner.

---

## 4 · Peer health (latency)

- Each node polls every **enabled** peer's `GET /members/{id}/latency` on the
  `ClusterPollMs` interval (default 10s), stores `latencyMs` + `status`.
- No response within timeout ⇒ `status: "unreachable"`. Disabled ⇒ no ping,
  `status: "disabled"`.
- **Two axes, never conflated (§2·b, P0.5):** the poll feeds the first-hand `Status`
  axis (reachable/unreachable/unknown) AND — on a successful probe of a peer that
  authenticates (`/identity` advertises `cluster` + a matching `apiVersion`) — promotes
  that peer to `alive` and stamps the last-evidence clock the gossip failure detector
  reads. `Status` is this node's own probe; `MembershipState` is the gossip-converged
  state; a latency/metrics row is never itself a status.
- The Cluster page reads `GET /members` (includes `latencyMs`, `status`, and
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
> assumes it. Authority: the cluster package's `docs/cluster-message-bus.md`.

### P0 — Peer foundation (membership + trust) · `planned`
The trust half already exists (the message-bus foundation built the
`Cluster__Secret` config + the `ClusterTokenService` mint/verify seam with
current+previous rotation). P0 adds the **membership** half — a manually-seeded
mesh that works fully before gossip lands:
- `Peers` table (id, url — the advertised client URL, gossipUrl?, nickname, nodeId,
  incarnation, status, membershipState, stateChangedAt, latencyMs, lastSeen,
  apiVersion, enabled). `status` (first-hand probe) and `membershipState`
  (gossip-converged, P0.5) are the two liveness axes, never conflated.
- `cluster` capability advertised by the **capability model** (present when
  `ClusterEnabled`), **not** a `LeafCatalog` entry — it has no systemd unit, so it
  must not render a phantom Services-board card.
- `PeersController`: CRUD + `GET /members/identity` (`{nodeId, apiVersion, build,
  capabilities}`) + `GET /members/{id}/latency`.
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
- The ephemeral `POST /api/v1/members/sync` roster-exchange endpoint — cluster-token
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
- **SPA-facing read (G1):** `GET /api/v1/members/roster` — the projection of
  the converged roster the browser reads to auto-populate its node registry (§7), on
  `api:members.read`. `GET /members` carries management detail (`enabled`, `apiVersion`) and
  takes `api:members.manage`; this is the lean
  `{ memberId, label, kind, clientUrl, membership, status, latencyMs }`, enabled members only,
  every membership state honestly labelled.
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

### P0.6 — Symmetric introduce and address reflection · `built`
Collapses cluster addressing to a single configured value — `Cluster__Secret` — by
making the join teach both nodes what they need, instead of requiring each node to be
pre-told its own address (§2 #13a-c).

- **A candidate says whether a browser can use it.** Each candidate is `{ url, client }`.
  The two reflection sources are browser-reachable by construction — an operator pastes a URL
  into a panel, and an observed host *is* a browser that arrived — so `client` is true for
  both; `Api__ClusterGossipUrl` and a peer-observed source address contribute node-only
  candidates. The roster's `clientUrl` is the best verified client candidate, and
  node-to-node traffic takes the best verified candidate of either kind.
- **One exchange, one record shape.** `POST /api/v1/members/introduce` carries an
  `IntroduceExchange` and answers with the same record — the push-pull idiom
  `/members/sync` already uses. Each side sends its own `NodeCard` (`nodeId`, `apiVersion`,
  `build`, `capabilities`, `candidates`, `incarnation`) and the address it reached the other
  at.
- **One predicate, called by both sides.** Token validity, `nodeId` is not our own, a
  matching `apiVersion`, the `cluster` capability, and a transport gate refusing plaintext
  to anything outside loopback and the private ranges. Initiator and receiver run the same
  function over the same record, so the two directions cannot drift apart.
- **Reflection carries the address.** The URL an operator pasted is the one address in the
  system a human has stated and a peer has just proven reachable, so the introducing node
  hands it back as `youAre` and the joining node adopts it as its first candidate. The
  reverse direction reflects the source address the receiver observed, labelled
  `observed` — a hint that seeds a candidate, never a URL anything depends on.
- **Verification is the probe that already exists.** Candidates land unverified;
  `PeerLatencyPoller` walks them in order and pins the first that answers `/identity` with
  the expected `nodeId`. Every candidate failing is an honest `unreachable`.
- **Where a member says it answers.** A configured public address leads, then the name the
  cluster assigned it and serves (`cluster-dns-plan.md`), then the member-only gossip
  address, then what reflection learned (`SelfIdentityStore`). Learned candidates persist,
  so a restart keeps them.
- **Panel origins come from the sign-in provider.** CORS admits the origins the cluster's
  sign-in provider has registered clients at, read through the holder of `auth` — so a
  panel served from somewhere that is not a node reaches every node without a per-node
  allowlist.
- **Order independence is the acceptance test.** Introducing B from A and introducing A
  from B leave the identical cluster state, and simultaneous mutual introduction leaves one
  roster row per node: both upserts key on `nodeId`, and incarnation ordering settles the
  rest.
- **Coordinated upgrade, refused loudly.** The records nodes exchange carry their own
  version (`ClusterProtocol.Current` on the node card), because `apiVersion` is the frozen
  `/api/v1` route segment and stays put across builds: two nodes can serve identical
  routes and still disagree about what a `SyncMember` contains. That disagreement is
  silent — the fields one side does not send arrive empty, so a peer joins and then
  quietly cannot be reached — so a mismatch is a `409 protocol_mismatch` at the join
  instead. Raise the number whenever a node-to-node record changes shape.
- **A node id is unique in the roster.** Two introductions arriving at once resolve the row
  under one gate and the column is uniquely indexed, so the same peer cannot be counted
  twice. The surplus of an older duplicate is dropped rather than reconciled: the roster is
  this node's own copy, and gossip refills it.

### P1 — Single sign-on · `built`
- **One session, every member.** The auth anchor mints it; each member verifies it against
  the anchor's published key and evaluates every request against its own replica of the
  cluster's authority (§0, §3). No member serves an `/auth` path.
- **Ending a session reaches every member.** The anchor announces an ended session as a
  durable `session.revoke`; each member's `SessionRevokeHandler` records it on the
  ended-session list and drops the cached answer, so the refusal is immediate. Durable to
  down nodes (outbox redelivery on return), and idempotent, so a replay is harmless.
- **Durable fan-out targets first-hand-`alive` peers only (locked).** The two
  transports split their target sets the same way §2·b G4 splits their retention:
  ephemeral gossip (`/members/sync`) reaches **any** enabled roster member, but a
  **durable**, identity-carrying message (`session.revoke` names a session) fans
  out **only** to peers this node has authenticated first-hand. First-hand-alive is
  **two** conditions, not one: `MembershipState == alive` **AND** `LastSeen` set — a
  purely gossip-learned peer is *stored* `alive` with a null `LastSeen` (it displays
  as the derived `joining`), and only this node's own probe / an authenticated inbound
  contact stamps `LastSeen`. That second condition is exactly what excludes an
  unconfirmed hearsay/phantom URL from receiving a secret-bearing message (or sitting
  in the outbox retrying to a corpse for the 7-day TTL). `RosterClusterTargetProvider`
  applies the filter; ephemeral gossip is unaffected (it reads `ListEnabledAsync`
  directly, not this provider).
- **Self-validated:** the deny-list across two nodes over the real bus (an ended session
  refused on the member that did not end it); the durable→alive filter (a hearsay/phantom
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
  /members/{id}/resources | /capabilities | /library` (service-token relay, reuse
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

### `GET /api/v1/members` (`api:members.manage`)
```json
{ "members": [ {
  "id": "abc123", "url": "https://node-b:8097", "nickname": "Gaming Box",
  "memberId": "node-b", "kind": "node", "status": "reachable",
  "membership": "alive", "latencyMs": 12, "lastSeen": "2026-07-10T12:00:00Z",
  "apiVersion": "v1", "enabled": true
} ] }
```
`url` is the advertised client URL (browser-reachable). `status` = this node's first-hand
probe; `membership` = the gossip-converged state (alive/suspect/dead/left, or the derived
`joining` for hearsay this node has not yet authenticated first-hand).

### `GET /api/v1/members/roster` (`api:members.read` — the SPA node list, G1)
The **browser-facing** projection of the converged roster: the read that powers "add
one, see all". `GET /members` carries management detail (the local handle, the `enabled`
flag, `apiVersion`) that belongs to whoever manages membership; this is the lean roster
a panel reads to auto-populate its node registry.
```json
{ "members": [ {
  "memberId": "node-b", "label": "Gaming Box", "kind": "node",
  "clientUrl": "https://node-b:8097", "membership": "alive",
  "status": "reachable", "latencyMs": 12
} ] }
```
- **Enabled members only.** Disabling is a management state under `api:members.manage` —
  a reader of the roster neither sees a disabled node nor is handed a URL to reach it. Every **membership** state is otherwise
  present (`alive`/`joining`/`suspect`/`dead`/`left`), honestly labelled, so the SPA renders
  a hearsay/`joining` or `suspect` node provisionally and decides for itself whether to
  auto-add it — the API never hides a state, only the management columns.
- `clientUrl` = the **advertised** browser-reachable URL (`PeerEntity.Url`), **never** the
  node-to-node gossip URL — the browser must be able to reach it directly (§2 #13a).
- `label` = `Nickname ?? MemberId`; `membership` = the same `GossipState.Display` derivation
  `MemberView` uses (yields `joining` for un-authenticated hearsay).
- **Self is not in the list** — a node is not its own peer; the SPA already holds the node it
  connected to. No `enabled` or `apiVersion` reaches a roster reader.

### `POST /api/v1/members` (`api:members.manage`)
```json
{ "url": "https://node-b:8097", "nickname": "Gaming Box" }
→ 201 { id, url, nickname, nodeId, apiVersion, status:"reachable", enabled:true }
→ 400 { error: { code: "invalid_url" } }
→ 409 { error: { code: "version_mismatch", details: { remote:"v2", local:"v1" } } }
→ 422 { error: { code: "peer_not_cluster",
        message: "Remote node does not advertise the cluster capability" } }
→ 422 { error: { code: "insecure_transport" } }
→ 409 { error: { code: "peer_is_self" } }
→ 502 { error: { code: "peer_unreachable" } }
```

### `POST /api/v1/members/introduce` (cluster-token authed)
The symmetric join exchange (§2 #6): the request body and the `200` body are the same
record, and the same validation predicate runs on both sides.
```json
{ "self": { "nodeId": "node-a", "apiVersion": "v1", "build": "0.1.0+abc123",
            "capabilities": ["monitor","watchdog","cluster"],
            "candidates": [{ "url": "https://node-a.example", "client": true }],
            "incarnation": 3, "protocol": 1 },
  "youAre": { "url": "https://node-b.example", "provenance": "operator" } }
→ 200  the same record from the other side; its `youAre` carries
       `"provenance": "observed"`, or is null when the receiver saw no usable address
→ 401 { error: { code: "invalid_cluster_token" } }
→ 409 { error: { code: "peer_is_self" } }
→ 409 { error: { code: "version_mismatch", details: { remote:"v2", local:"v1" } } }
→ 409 { error: { code: "protocol_mismatch" } }
→ 422 { error: { code: "peer_not_cluster" } }
→ 422 { error: { code: "insecure_transport" } }
```

### `GET /api/v1/members/identity` (cluster-token authed)
```json
{ "nodeId": "node-b", "apiVersion": "v1", "build": "0.1.0+abc123",
  "capabilities": ["monitor","watchdog","cluster"],
  "candidates": [{ "url": "https://node-b.example", "client": true }], "protocol": 1 }
```

### `GET /api/v1/members/self/resources` (cluster-token authed + disable-gated)
What this node exposes to a cluster peer's server-side fan-out. Cluster-token authed with the same
fail-closed preamble as `/members/inbox`, and — unlike `/members/identity` (token-only, so a not-yet-joined
node can still identify itself) — a resource read IS disable-gated: an explicitly-disabled peer gets
`403 peer_disabled`. A lean projection of the §4·a host capacity strip; `cpuPct`/`mem`/`disks` are honest
`null` when no metrics snapshot exists (never fabricated — the "metric-presence ≠ status" invariant).
```json
{ "id": "node-b", "label": "Gaming Box", "status": "online",
  "cpuPct": 37, "mem": { "used": 9.2, "total": 32 },
  "disks": [ { "mount": "/", "used": 180, "total": 512 } ] }
```

(`/members/self/capabilities` and `/members/self/library` reuse the existing
capability and `LibraryEntry` shapes verbatim, same auth + gate.)

### `GET /api/v1/members/{id}/{resources|capabilities|library}` (`api:members.manage` — the relay)
The **server-side node-proxy** (the one node-proxied path): mints a cluster service token and GETs peer
`{id}`'s `self/*` surface, returning the peer's body **verbatim**. Consumed by the
capacity fan-out / the assistant, **never the SPA** (§8). `{id}` is the roster-row id (as `/{id}/latency`).
Honest degradation, never a 500: `404` unknown id, `403 peer_disabled`, `502 peer_unreachable` (down peer
or non-2xx).

### `POST /api/v1/members/inbox` (cluster-token authed)
The message-bus receive endpoint — one endpoint, typed envelope. Full contract:
the cluster package's `docs/cluster-message-bus.md`.

---

## 8 · SPA changes

> **The SPA-side authority is `kgsm-web/src/lib/CLAUDE.md`** — the SPA's cluster surface (discovery,
> node-roster registration, and the readable node list it depends on). The bullets below are
> the API-side summary.

- Rename the `localStorage` connection registry `hosts → nodes` (same structure;
  provide a one-time in-place migration so existing connections survive the
  rename).
- **Cluster discovery = the browser-side mirror of backend gossip.** Once the SPA
  connects+auths to **one** node, it pulls that node's converged roster and
  auto-populates the `nodes` registry with the whole cluster (using each entry's
  **advertised client URL**, §2 #13a) — "add one, see all" for humans. A new user
  who signs in is shown the cluster already assembled, each node gated on what they
  may do there; no per-node setup. This depends on the roster carrying
  browser-reachable URLs + each peer allowing the SPA origin via CORS (the
  preflight-probe warning below).
- **Fleet page → Cluster page** (new route, replaces the old):
  - Lists the local node + all peers; add/remove/enable/disable; latency;
    reachable/unreachable/disabled status.
  - **No resource dashboard** — per-node resources stay on the per-node pages.
- **Single sign-on:** one sign-in at the auth anchor; the SPA presents that one
  session to every node. "Sign out everywhere" is the anchor's, and reaches every
  member over the bus.
- **Cross-node install:** the install modal's node dropdown is populated from the
  cached Cluster-page data. Selecting a peer switches the SPA to talk **directly**
  to that peer with the same session; the source node drops out of the loop.
- **CORS follows the sign-in provider's clients:** a peer admits the SPA's origin
  because the SPA is a registered client of the cluster's provider, whose origins
  every member reads through the holder (`hosted-sign-in-plan.md` decision 18). The
  Cluster page runs a browser-side preflight probe per peer and **warns** on a
  CORS/reachability mismatch rather than failing opaquely mid-install.

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

### P0.6 — self-validated (`SymmetricIntroduceTests`, plus the suite it runs in)
- The same predicate refuses a mismatched `apiVersion`, a mismatched cluster protocol, a
  missing `cluster` capability, a self-introduction and a plaintext public candidate —
  whichever side is asked.
- A node never configured with an address adopts the operator-pasted URL from its first
  introduction and gossips it onward.
- Introducing B from A and introducing A from B leave the two nodes holding the same
  roster; simultaneous mutual introduction leaves one row per node, not two.
- An unverified candidate is never reported `alive`, and a node whose candidates all fail
  reports `unreachable` while holding no invented address.

### P1
- Sign in at the anchor; the one session reads A and B.
- An ended session is refused on A **and** B.
- Down-node revocation: stop B, end the session at the anchor, restart B → the queued
  `session.revoke` is delivered on B's return (bus redelivery), session refused.

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
2. **Topology: LAN vs WAN, and the two-URL split.** `GET /members/identity` is
   reachable **before** any trust is established (you can't authenticate before you
   know the peer). If nodes span the public internet, that endpoint is an
   internet-exposed enumeration/DoS surface — decide LAN-only (VPN/overlay) vs WAN,
   and rate-limit `/identity` + `/inbox` + `/sync` accordingly. Ties into who opens
   the cross-node port (kgsm-firewall/watchdog own port-opening). Addressing itself is
   settled (§2 #13a-c, P0.6): candidates are reflected at join and probed before use, so
   neither URL knob is required to form a cluster. What remains open here is the exposure
   question — whether `/identity`, `/inbox` and `/sync` face the public internet at all,
   and the rate limits if they do.
3. **TLS cert validation.** Over the shared-secret model, is TLS validated
   (proper certs / internal CA) or is it encryption-only with the service token as
   the sole identity layer? Pin one; if self-signed, document the accepted risk.
4. **Placement soft-reservation.** #24 accepts the TOCTOU race for MVP; revisit if
   double-booking bites in practice (a short-lived reservation on the target).
5. **Nickname divergence.** Nicknames are node-local; the SPA aggregates — decide
   which label wins when two nodes name the same peer differently (cosmetic).
6. **`peer_disabled` after cluster opt-out.** A node that drops the `cluster`
   capability while peers still list it: its `/members/self/*` and `/inbox` must
   `404`/`403` cleanly, and peers must reflect it as unavailable, not fabricate a
   status.
