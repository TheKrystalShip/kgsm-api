# CLAUDE.md — Realtime/

The per-host realtime stream — `GET /api/v1/stream`, fetch-based SSE (M2; migrated off WebSocket
2026-07-02, `sse-migration-plan.md`). Built; the contract is frozen in `PLAN.md §6` (stream row) +
`§8` (M2 log). This file is the local "what you must not break."

## Locked decisions (do not relitigate)

- **Hand-rolled SSE, NOT SignalR.** The `{ topic, type, data }` envelope **is** the contract —
  SignalR's framing would break it. Don't introduce SignalR. **Fetch-based SSE, not native
  `EventSource`** — `EventSource` can't set an `Authorization` header (see Auth below). Topics are
  chosen at connect via `?topics=a,b,c` (comma-separated); subscriptions are **immutable per
  connection** (fixed from the query at connect, no client→server channel) — changing topics means
  opening another stream. Wire frame: `data: <json>\n\n` (no `event:`/`id:`/`Last-Event-ID`); a
  `: connected\n\n` comment on connect and a `: keepalive\n\n` comment every 20s (also the dead-client
  detector, alongside `RequestAborted`).
- **All topic/type strings live in `StreamProtocol.cs` — never inline** (a standing user requirement).
  Add a new topic or message type there; a literal `"servers"` or `"metrics.tick"` anywhere else is a bug.
- **Patch-only, no snapshot-on-subscribe.** The client hydrates via REST and applies patches (§3·j);
  on (re)connect it re-hydrates via REST. Don't send a full snapshot when a client subscribes.
- **Coalesce-to-latest per key** is the backpressure rule: a slow client gets the *newest* frame, never
  an unbounded backlog; a stalled send is torn down → the client reconnects (§3·j). Don't buffer history.
  **Exception — the `audit` topic (M5):** audit appends are distinct immutable facts, not supersede-by-latest
  patches, so each carries a **unique** coalesce key (the event id, `StreamProtocol.AuditEntityKey`) — never
  the static topic name, which would silently drop all but the latest append. The client prepends; on
  reconnect it re-hydrates via `GET /audit` (the stream stays patch-only, no replay).

## Invariants when you touch this

- **The `servers` topic carries status/roster ONLY — never the 1s metric firehose.** Resource ticks
  live on `servers/{id}/metrics`. `DomainPump`'s change-detection deliberately ignores the metrics block
  so it never double-streams. Breaking this floods the status topic (smoke check 22 guards it).
- **`network.patch` rides its OWN topic `servers/{id}/network` (M6·b) — never `server.patch`.** The same
  topic-separation discipline as metrics: keeping the firewall block off the `servers` topic is what lets
  `server.patch` stay the frozen M1·b `Server`. **No pump publishes it** — it is pushed ONLY by the
  `open_ports` verify (the firewall is socket-activated + idle-exits; a periodic probe would defeat that).
  Don't add a network pump; don't fold `network` into `server.patch`.
- **One shared `MetricsMapping`** makes a stream tick byte-identical to the REST element it patches —
  REST and the stream must not drift. Map in one place.
- **Honesty: monitor-down → metric topics go silent**, never a replayed stale frame. The
  `hosts/{id}/capabilities` `down` flip (with `provisioned:true` — capability never "lost") is what
  *explains* the silence. Never synthesize a tick to fill a gap.
- **The pumps:** `MetricsPump` (live monitor scrape) + `DomainPump` (instance roster/run-state) are
  **gated on subscribers** (idle stream costs nothing). Both intervals are **configurable** (`ApiOptions`):
  `KGSM_API_METRICS_POLL_MS` (default **1s** — the live charts feed, keep it tight) and
  `KGSM_API_DOMAIN_POLL_MS` (default **5s**, relaxed — each tick spawns `kgsm.sh` and the roster changes
  rarely; operator actions push an immediate verify patch off the command path, so this only catches
  out-of-band changes). `LeafHealthMonitor` is **always-on** (~2s) — the single source feeding both this
  stream's `capabilities.patch` and the REST `GET /hosts` capability block, so they can't disagree.

## Auth (M4·a, header bearer since the SSE migration)

`/stream` is `[Authorize(Policy = viewer)]`. Fetch-based SSE sends the bearer as a normal
`Authorization: Bearer` header through the standard JwtBearer pipeline — no query-string token hack
(the old `?access_token=`/`OnMessageReceived` carve-out is **removed**; a query token alone no longer
authenticates, tested). This is *why* SSE replaced the WS transport: a browser can't set a header on a
WS handshake, and a handshake `401` was an opaque `1006` close the client couldn't read, forcing
client-side expiry prediction. SSE exposes a **readable `401`** so the stream heals through the same
reactive rotate-on-401 path as every REST call — **don't reintroduce client-side expiry math for this
endpoint.**

**Operator-only topics** (`hosts/{id}/logs`) requested by a non-operator are **silently dropped** from
the connection's subscription set at connect (`StreamController` filters via
`StreamProtocol.RequiresOperator`) — never a 403 on the whole stream.
