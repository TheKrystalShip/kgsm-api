# CLAUDE.md — Realtime/

The per-host realtime stream — `GET /api/v1/stream`, fetch-based SSE. The contract is frozen in
`PLAN.md §6` (stream row). This file is the local "what you must not break."

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
  **Exception — the `audit` topic:** audit appends are distinct immutable facts, not supersede-by-latest
  patches, so each carries a **unique** coalesce key (the event id, `StreamProtocol.AuditEntityKey`) — never
  the static topic name, which would silently drop all but the latest append. The client prepends; on
  reconnect it re-hydrates via `GET /audit` (the stream stays patch-only, no replay).

## Invariants when you touch this

- **The `servers` topic carries status/roster ONLY — never the 1s metric firehose.** Resource ticks
  live on `servers/{id}/metrics`. `DomainPump`'s change-detection deliberately ignores the metrics block
  (and `diskBytes`, which is one) so it never double-streams. Breaking this floods the status topic
  (a smoke check guards it).
- **Two metric topics, split by what is asking.** `servers/{id}/metrics` is one chart's feed, at the
  scrape cadence. `servers/metrics` is one frame for the WHOLE roster on a 2s card cadence
  (`MetricsPump.RosterIntervalMs`) — what a grid of server cards reads, because a client that opens a
  connection per resource-scoped topic cannot subscribe to N of them. Its row is the live half of the
  REST hydrate: `{ id, metrics, diskBytes }`, the same two parts `Server` carries, so a merge is
  field-for-field. **A row may be half-null** — a stopped instance has no sample and a real footprint,
  which is the whole reason disk sits outside the metrics block. Never fill either half in.
- **`network.patch` rides its OWN topic `servers/{id}/network` — never `server.patch`.** The same
  topic-separation discipline as metrics: keeping the firewall block off the `servers` topic is what lets
  `server.patch` stay the frozen `Server`. **No pump publishes it** — it is pushed ONLY by the
  `open_ports` verify (the firewall is socket-activated + idle-exits; a periodic probe would defeat that).
  Don't add a network pump; don't fold `network` into `server.patch`.
- **One shared `MetricsMapping`** makes a stream tick byte-identical to the REST element it patches —
  REST and the stream must not drift. Map in one place.
- **Honesty: monitor-down → metric topics go silent**, never a replayed stale frame. The
  `hosts/{id}/capabilities` `down` flip (with `provisioned:true` — capability never "lost") is what
  *explains* the silence. Never synthesize a tick to fill a gap.
- **The pumps:** `MetricsPump` (live monitor scrape) + `DomainPump` (instance roster/run-state) are
  **gated on subscribers** (idle stream costs nothing). Both intervals are **configurable** (`ApiOptions`):
  `Api__MetricsPollMs` (default **1s** — the live charts feed, keep it tight) and
  `Api__DomainPollMs` (default **5s**, relaxed — each tick spawns `kgsm.sh` and the roster changes
  rarely; operator actions push an immediate verify patch off the command path, so this only catches
  out-of-band changes). `LeafHealthMonitor` is **always-on** (~2s) — the single source feeding both this
  stream's `capabilities.patch` and the REST `GET /hosts` capability block, so they can't disagree.

## Auth

`/stream` is `[Authorize(Policy = viewer)]`. Fetch-based SSE sends the bearer as a normal
`Authorization: Bearer` header through the standard JwtBearer pipeline — a query-string token
authenticates nothing (regression-pinned: `Stream_Sse_QueryTokenIgnored`). SSE exposes a
**readable `401`**, so the stream heals through the same reactive rotate-on-401 path as every
REST call — **don't introduce client-side expiry math for this endpoint.**

**The connection re-checks its own session every 20s.** `[Authorize]` gates the CONNECT and nothing in
the framework re-runs it on a request that lasts hours, so `StreamController` hands the connection a
probe over `ISessionValidator` and the write loop ends the stream once the `sid` stops being valid —
a revoke reaches the live channel in ≤20s, the same order as REST's ≤5s. Two things about it are
load-bearing: it runs on the **loop's own clock**, not inside the heartbeat branch (a busy stream is
woken by frames faster than any delay completes, so a duty hung off that branch never fires on the
connections carrying the most data), and it checks the **session, not the token's `exp`** — tearing a
stream down when the access token lapses would churn every client four times an hour and surface a
reconnect banner each time, for a credential the client is about to rotate anyway. A check that
THROWS ends the stream too: "couldn't measure" is not "still valid", and the redial re-runs the full
auth pipeline, which is the authority. No `sid` (auth-disabled) → no probe, unchanged behaviour.

**Operator-only topics** (`hosts/{id}/logs`) requested by a non-operator are **silently dropped** from
the connection's subscription set at connect (`StreamController` filters via
`StreamProtocol.RequiresOperator`) — never a 403 on the whole stream.
