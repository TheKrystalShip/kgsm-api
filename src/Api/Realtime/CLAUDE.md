# CLAUDE.md — Realtime/

The per-host realtime stream — `GET /api/v1/stream`, a raw WebSocket (M2). Built; the contract is
frozen in `PLAN.md §6` (WS stream row) + `§8` (M2 log). This file is the local "what you must not break."

## Locked decisions (do not relitigate)

- **Raw ASP.NET Core WebSockets, NOT SignalR.** The hand-rolled `{ topic, type, data }` envelope **is**
  the contract — SignalR's framing would break it. Don't introduce SignalR.
- **All topic/type strings live in `StreamProtocol.cs` — never inline** (a standing user requirement).
  Add a new topic or message type there; a literal `"servers"` or `"metrics.tick"` anywhere else is a bug.
- **Patch-only, no snapshot-on-subscribe.** The client hydrates via REST and applies patches (§3·j);
  on (re)connect it re-hydrates via REST. Don't send a full snapshot when a client subscribes.
- **Coalesce-to-latest per key** is the backpressure rule: a slow client gets the *newest* frame, never
  an unbounded backlog; a stalled send is torn down → the client reconnects (§3·j). Don't buffer history.
  **Exception — the `audit` topic (M5):** audit appends are distinct immutable facts, not supersede-by-latest
  patches, so each carries a **unique** coalesce key (the event id, `StreamProtocol.AuditEntityKey`) — never
  the static topic name, which would silently drop all but the latest append. The client prepends; on
  reconnect it re-hydrates via `GET /audit` (the WS stays patch-only, no replay).

## Invariants when you touch this

- **The `servers` topic carries status/roster ONLY — never the 1s metric firehose.** Resource ticks
  live on `servers/{id}/metrics`. `DomainPump`'s change-detection deliberately ignores the metrics block
  so it never double-streams. Breaking this floods the status topic (smoke check 22 guards it).
- **`network.patch` rides its OWN topic `servers/{id}/network` (M6·b) — never `server.patch`.** The same
  topic-separation discipline as metrics: keeping the firewall block off the `servers` topic is what lets
  `server.patch` stay the frozen M1·b `Server`. **No pump publishes it** — it is pushed ONLY by the
  `open_ports` verify (the firewall is socket-activated + idle-exits; a periodic probe would defeat that).
  Don't add a network pump; don't fold `network` into `server.patch`.
- **One shared `MetricsMapping`** makes a WS tick byte-identical to the REST element it patches —
  REST and WS must not drift. Map in one place.
- **Honesty: monitor-down → metric topics go silent**, never a replayed stale frame. The
  `hosts/{id}/capabilities` `down` flip (with `provisioned:true` — capability never "lost") is what
  *explains* the silence. Never synthesize a tick to fill a gap.
- **The pumps:** `MetricsPump` ~1s + `DomainPump` ~3s are **gated on subscribers** (idle stream costs
  nothing); `LeafHealthMonitor` is **always-on** (~2s) — the single source feeding both this stream's
  `capabilities.patch` and the REST `GET /hosts` capability block, so they can't disagree.

## Auth (M4·a)

`/stream` is `[Authorize(Policy = viewer)]`. The browser can't set an `Authorization` header on a WS
handshake, so the bearer rides **`?access_token=`** — read in JwtBearer's `OnMessageReceived` (in
`Startup`) for the `/stream` path only. Authorize at the handshake; **don't tear down a live socket on
mid-stream token expiry**. The `StreamController` WS-upgrade check still emits the `bad_request` envelope
for a plain (non-upgrade) GET — but only after authorization passes.
