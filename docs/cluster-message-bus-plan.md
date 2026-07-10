# Cluster Message Bus — Transactional Outbox / Inbox

> **Authority for node-to-node messaging in a KGSM cluster.** A reliable,
> at-least-once, idempotent mail channel between `kgsm-api` nodes over plain HTTP —
> no broker, no per-message endpoint. It is the **foundation** the peer-federation
> features ride on (`PLAN-peers.md`); it is built **first**, and its first customer
> is cluster-wide logout.
>
> Read `PLAN-peers.md §0` (the one-guild trust boundary) and §3 (the cluster
> service token) first — this spec assumes both.

---

## Status legend
`built` = exists & verified · `partial` = exists, incomplete · `planned` =
designed, not built · `open` = not yet decided. **Status per phase: see §12** —
M-bus·a (core transport) is `built`; M-bus·b/c are `planned`.

---

## 1 · Why this exists

Some node-to-node actions must survive a node being **down** at the moment they are
issued. The archetype: a user clicks **"Sign out everywhere."** The revocation must
reach every node in the cluster — including one that is currently offline — and take
effect when it returns. A fire-and-forget HTTP POST loses that message; a
synchronous fan-out fails the whole operation on one unreachable node.

The industry answer is the **Transactional Outbox + Inbox pattern**: persist the
intent locally in the same transaction as the state change, then deliver it with
at-least-once retries to an idempotent receiver. This spec is that pattern, sized
for a small single-owner cluster that already has HTTP and SQLite and follows the
"no extra infrastructure daemons" doctrine (`system-architecture.md §4`).

**Non-goals:** this is **not** a broker, not a general pub/sub, not an
exactly-once/ordered-delivery system, and not a request-reply channel. Synchronous
node-to-node calls that need a response (peer resource queries, the
`/auth/cluster-session` vouch) stay ordinary HTTP GET/POST — they do **not** use the
bus. The bus is exclusively for **asynchronous, reply-free, must-not-be-lost**
notifications.

---

## 2 · Design principles

1. **Reuse what exists** — HTTP for transport, SQLite (`EnsureCreated`, per the
   kgsm-api convention) for durability. No RabbitMQ/NATS/Kafka, no etcd/Consul.
2. **One endpoint, typed messages.** `POST /api/v1/peers/inbox` takes a
   discriminated-union envelope. New capabilities add a **message type**, never a
   new endpoint.
3. **Transactional durability.** The outbox row is written in the **same DB
   transaction** as the local effect it announces — if the local action commits, the
   message is guaranteed to eventually send; if it rolls back, no ghost message.
4. **At-least-once + idempotent apply.** Delivery may repeat; the receiver dedupes
   by message id **and** every handler is written to be idempotent. Exactly-once is
   not attempted (it is unachievable over an unreliable network).
5. **Order-independent.** Messages carry no global order. Handlers must be
   commutative (revocation is). A future type needing order carries its own sequence
   and orders in its handler — out of scope here.
6. **Liveness is borrowed, not built.** The §4 latency poller already knows which
   peers are reachable; the outbox drainer uses it. No separate heartbeat.
7. **Fail-open on availability, fail-closed on auth.** A down target ⇒ queue and
   retry (never an error to the caller). A bad service token on the inbox ⇒ reject
   (`401/403`), never process. Two distinct code paths (`PLAN-peers.md §2 #25/#26`).

---

## 3 · The envelope

```jsonc
{
  "id":      "5f2c…",            // UUID — the dedupe key, minted by the sender
  "type":    "session.revoke",   // discriminated union tag
  "from":    "node-a",           // sender nodeId — MUST equal the service token's iss
  "ts":      "2026-07-10T12:00:00Z",
  "payload": { … }               // type-specific, camelCase, ISO-8601 Z timestamps
}
```

- `id` is generated once by the sender and is stable across every retry — it is what
  makes redelivery idempotent. It is also the replay-defense: a captured-and-replayed
  envelope is a duplicate id and is ack'd without re-applying.
- `from` **must** match the authenticated service token's `iss`; a mismatch is a
  `403` (a node may not send "as" another node).
- `ts` is informational (diagnostics/ordering hints); correctness never depends on it.
- Max envelope size is bounded (default 64 KiB) — reject larger with `413`.

### Message-type registry (the discriminated union)

| `type` | Direction | Payload | Handler effect (idempotent) |
|---|---|---|---|
| `session.revoke` | any → any | `{ scope:"all"\|"user"\|"sid", discordId?, sid? }` | Revoke matching `SessionEntry` rows locally. **First customer.** |
| `node.online` | any → all peers | `{ nodeId }` | Trigger an immediate outbox flush toward `nodeId` (a latency optimization — see §6). Safe to drop. |
| `peer.added` / `peer.removed` | reserved | `{ nodeId, url }` | (Later) keep peer lists loosely consistent. |
| `audit.forward` | reserved | `{ … }` | (P5) cross-node audit streaming. |

Adding a type = add a row here + a handler in the dispatch map (§7). **Unknown type
on receipt ⇒ ack (`200`) + a loud log, do not retry-forever** (§8) — the additive-only
`/api/v1` contract means a newer sender may legitimately know a type an older peer
does not; dropping it is the safe, non-blocking behavior.

---

## 4 · The wire

### `POST /api/v1/peers/inbox` (cluster-token authed)

```
Authorization: Bearer <cluster service JWT>   (PLAN-peers.md §3)
Content-Type: application/json
Body: <envelope>

→ 200 { "status": "accepted" }      // newly applied, or a de-duplicated replay
→ 400 { error: { code: "bad_request" } }          // malformed envelope
→ 401 { error: { code: "invalid_cluster_token" } }
→ 403 { error: { code: "peer_disabled" } }         // iss not an enabled peer
→ 403 { error: { code: "from_mismatch" } }         // from ≠ token iss
→ 413 { error: { code: "payload_too_large" } }
→ 500 { error: { code: "internal" } }              // handler failed → sender retries
```

- `200` for both a fresh apply and a duplicate — the sender cannot and need not tell
  them apart; both mean "you can stop retrying."
- `500` is the **only** response that keeps the message in the sender's outbox. It is
  reserved for a *transient* handler failure (DB locked, etc.). A message the receiver
  can never apply (unknown type, malformed payload for a known type) is **not** a
  `500` — it is a `200`-drop or `400`, so it never wedges the sender's queue.
- All non-2xx use the frozen `{error:{code,message,details?}}` envelope
  (`kgsm-api` invariant #4) via the existing `InvalidModelStateResponseFactory` +
  `UseStatusCodePages`.

---

## 5 · Data model (SQLite, `EnsureCreated`)

Two tables — the API's own operational metadata, same category as the audit log and
session registry (`kgsm-api` invariant: persistence is downstream of the stateless
engine; the API persists only its own state). **A schema change means wiping the dev
DB**, not adding an EF migration (`kgsm-api` Gotchas).

### `Outbox` — one row per (message, target)

A broadcast to N peers enqueues **N rows**, each delivered and retried
independently.

| Column | Type | Notes |
|---|---|---|
| `Id` | TEXT PK | `<messageId>:<targetNodeId>` — unique per delivery |
| `MessageId` | TEXT | the envelope `id` (same across all targets of a broadcast) |
| `TargetNodeId` | TEXT | recipient |
| `Type` | TEXT | envelope type |
| `Payload` | TEXT | serialized envelope payload |
| `Status` | TEXT | `pending` \| `delivered` \| `dead` |
| `Attempts` | INT | incremented per failed send |
| `NextAttemptAt` | TEXT (UTC) | drainer skips rows in the future |
| `CreatedAt` | TEXT (UTC) | for TTL/dead-lettering |
| `DeliveredAt` | TEXT (UTC)? | set on `2xx` |
| `LastError` | TEXT? | last transient failure, for diagnostics |

### `Inbox` — processed-id ledger (dedupe)

| Column | Type | Notes |
|---|---|---|
| `Id` | TEXT PK | the envelope `id` — the dedupe key |
| `FromNodeId` | TEXT | sender, for diagnostics |
| `Type` | TEXT | for diagnostics |
| `ReceivedAt` | TEXT (UTC) | |
| `ProcessedAt` | TEXT (UTC)? | set only **after** the handler succeeds |

---

## 6 · The outbox drainer (background service)

A hosted service in the mould of `LeafHealthMonitor` / `AlertEngine` (an
`IHostedService`, one per node, DI-scoped DB access per pass).

### Send path (enqueue)
`IClusterBus.Enqueue(type, payload, targets)` writes one `Outbox` row per target
**inside the caller's transaction**. For "logout everywhere," `targets` = every
enabled peer + (optionally) self. The call returns as soon as the rows are committed
— delivery is asynchronous.

### Drain loop
Each tick (default **1s**, and immediately on a wake signal):
1. Select `pending` rows where `NextAttemptAt ≤ now` **and** the target is an
   **enabled** peer, grouped by target.
2. Per target (bounded parallelism across targets, serial within a target):
   `POST {target}/api/v1/peers/inbox` with a freshly-minted cluster service token.
   - **`2xx`** → `Status=delivered`, `DeliveredAt=now`.
   - **transient failure** (connect refused, timeout, `5xx`) → `Attempts++`,
     `NextAttemptAt = now + backoff(Attempts)`, `LastError=…`. Row stays `pending`.
   - **permanent reject** (`400/401/403/413`) → `Status=dead` + a loud log. The
     target will never accept it; retrying is pointless. (`401/403` here means a
     **local** misconfiguration — a wrong secret or a peer that disabled us — surfaced
     loudly, not silently retried.)
3. A row older than the **retry TTL** (default **7 days**) still `pending` →
   `Status=dead` + loud log. Seven days covers any realistic node outage; a revoke
   still queued after a week is an operational alarm, not a silent loss.

### Backoff
Capped exponential with jitter: `min(cap, base · 2^Attempts) ± jitter`, defaults
`base=1s`, `cap=5min`. Jitter avoids a thundering herd when many nodes recover at
once.

### Liveness coupling (the "back online" optimization)
The durable retry loop is the **correctness** mechanism — it delivers eventually
regardless. Two things merely make it **faster**, and neither is load-bearing:
- The §4 latency poller flipping a peer `unreachable → reachable` **wakes** the
  drainer and resets `NextAttemptAt=now` for that target's `pending` rows.
- A received `node.online` message does the same for that node.

If both signals are lost, the next backoff tick still delivers. This is the
deliberate split: **durable outbox for correctness, online-signal for latency.**

---

## 7 · The inbox handler (receive)

```
1. Authenticate: verify the cluster service token (fail-closed → 401/403).
2. Validate: envelope well-formed; from == token.iss (else 403); size ≤ limit.
3. Dedupe + apply, in one DB transaction:
     a. INSERT Inbox row by envelope id.
          - PK conflict ⇒ duplicate:
              • if ProcessedAt set → ack 200 (already applied)
              • if ProcessedAt null → another in-flight attempt; ack 200
                (the effect is idempotent; the original attempt owns completion)
     b. Dispatch to the handler for `type`:
          - unknown type → commit the Inbox row as processed, ack 200, loud log
            (drop; do not 500 — never wedge the sender)
          - known type → run the handler (itself idempotent)
     c. On handler success → set ProcessedAt = now, commit, ack 200.
     d. On transient handler failure → roll back (ProcessedAt stays null),
        respond 500 so the sender retries.
```

**Two layers of idempotency, on purpose:** the processed-id ledger stops re-apply in
the normal case; idempotent handlers make a re-apply harmless even across a crash
between "apply" and "record processed." `session.revoke` is naturally idempotent —
revoking an already-revoked (or absent) session is a no-op.

### Retention / GC
A GC worker (mirroring the existing session GC cadence, ~10 min) prunes:
- `Outbox` rows `delivered`/`dead` older than a retention window;
- `Inbox` rows older than the window.

The window **must exceed the outbox retry TTL** (≥ 7 days + margin, default **30
days**) so a late redelivery of a long-retried message is still recognized as a
duplicate rather than re-applied.

---

## 8 · Delivery semantics (stated plainly)

- **At-least-once.** A message is delivered one or more times; the receiver applies
  it exactly once via the dedupe ledger + idempotent handler.
- **No ordering.** Independent messages may arrive in any order. Handlers are
  commutative.
- **No exactly-once, by design.** Unachievable over an unreliable network; the
  idempotent-apply contract makes it unnecessary.
- **Poison-message safety.** A message the receiver can never accept is dropped
  (unknown type) or dead-lettered (permanent reject / TTL) — it can never block the
  queue behind it or retry forever.

---

## 9 · Security

- **Inbox auth is fail-closed:** cluster service token required; `iss` must be an
  enabled peer; `from` must equal `iss`. No token, wrong secret, disabled peer, or
  `from` spoof ⇒ reject, never process.
- **Replay:** a replayed envelope is a duplicate `id` → ack'd without effect. No
  separate nonce/timestamp window needed for the bus (the dedupe ledger subsumes it).
- **Blast radius:** the bus grants a valid cluster member the ability to trigger any
  registered handler (e.g., revoke any session). This is within the accepted
  one-guild-single-owner trust boundary (`PLAN-peers.md §0`) — the bus adds no
  authority a cluster member does not already have.
- **DoS surface:** `/peers/inbox` is authenticated, size-limited (`413`), and
  rate-limited per peer. It is not an unauthenticated endpoint (unlike
  `/peers/identity`, whose exposure is tracked in `PLAN-peers.md §10`).
- **No secrets in payloads.** Envelopes carry identifiers and intents, never tokens
  or credentials.

---

## 10 · Observability

Structured logs + counters (journald, per the logging convention):
- outbox depth (`pending` count) per target,
- delivery attempts / successes / transient-failures / dead-letters,
- delivery latency (enqueue → `delivered`),
- inbox receives / duplicates-deduped / unknown-type drops.

A non-zero `dead` count or a growing `pending` backlog toward a specific peer is the
operational signal that that peer is genuinely unreachable beyond the TTL.

---

## 11 · Self-validation plan

A `scripts/`-level smoke (two embedded API instances, or one instance + a stub peer)
proving:

1. **Happy path:** enqueue → target online → delivered, `2xx`, applied once.
2. **Down-then-up (the reason this exists):** stop the target, enqueue a
   `session.revoke`, confirm the row stays `pending` and retries; restart the target;
   confirm redelivery and that the session is revoked on it.
3. **Idempotent redelivery:** deliver the same envelope id twice → applied once,
   second is a de-duplicated `200`.
4. **Auth fail-closed:** POST `/inbox` with no token / wrong secret / a disabled-peer
   `iss` / a `from`≠`iss` → `401`/`403`, no effect.
5. **Unknown type:** send an unregistered `type` → `200` + logged drop, sender does
   not retry.
6. **Poison/TTL:** a permanently-rejected message → `dead`, loud log, queue behind it
   still drains.
7. **Transactional durability:** a local action that rolls back leaves **no** outbox
   row (no ghost message).

---

## 12 · Phasing

### M-bus·a — Core transport · `built`
Envelope + `POST /peers/inbox` + `Outbox`/`Inbox` tables + drainer (backoff, TTL,
dead-letter) + inbox dedupe/dispatch + `IClusterBus.Enqueue` + GC. One live type:
`session.revoke`. Self-validated: unit + two-node in-process (happy path, down-then-up
redelivery, auth fail-closed).

### M-bus·b — Liveness coupling · `planned`
Wire the latency-poller `reachable` flip and the `node.online` message to wake/flush
the drainer. Pure optimization; `a` is correct without it.

### M-bus·c — Reserved types · `planned`
`peer.added`/`peer.removed`; the `audit.forward` hook for P5. Each is a registry row
+ an idempotent handler; no transport change.

---

## 13 · Open items

1. **Anti-entropy backstop (optional).** Beyond per-message retry, a periodic
   reconcile ("send me your revocation tombstones since T") would catch anything ever
   dropped by a dead-letter. Deferred — the 7-day TTL + loud dead-letter alarm is the
   MVP safety net; add reconcile only if a real loss is observed.
2. **Self-delivery.** Whether "logout everywhere" enqueues a bus message to the
   origin node too, or revokes locally in-process and enqueues only to peers (likely
   the latter — no reason to round-trip to self). Settle when wiring P1.
3. **Rate-limit thresholds** for `/inbox` — pick concrete numbers during build once
   real message volumes are known.
4. **Clock skew on `ts`.** `ts` is informational today; if a future ordered type
   depends on it, revisit against the cluster clock-skew decision
   (`PLAN-peers.md §10`).
