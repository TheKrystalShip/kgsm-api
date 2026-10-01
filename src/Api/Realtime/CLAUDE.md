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
- **`me` is delivered by AUDIENCE, not by subscription alone.** Every other topic is host-wide: anyone
  subscribed gets the frame. This one carries the *reader's own* standing (`me.patch` with the account's
  `status`, `me.access` with the whole `GET /me/access` answer), so it goes only to the connections
  authenticated as the account it is about — `StreamHub.PublishToAccount`, matched on the **account id**
  so a session established through a linked provider identity is reached as readily as one established
  with a password. A connection that proves no account belongs to nobody and is never a recipient.
  Broadcasting it would hand every reader a directory of who holds what.
- **One shared `MetricsMapping`** makes a stream tick byte-identical to the REST element it patches —
  REST and the stream must not drift. Map in one place.
- **Honesty: monitor-down → metric topics go silent**, never a replayed stale frame. The
  `hosts/{id}/capabilities` `down` flip (with `provisioned:true` — capability never "lost") is what
  *explains* the silence. Never synthesize a tick to fill a gap.
- **The pumps:** `MetricsPump` (live monitor scrape) + `DomainPump` (instance roster/run-state) are
  **gated on subscribers** (idle stream costs nothing). Both intervals are **configurable** (`ApiOptions`):
  `Api__MetricsPollMs` (default **1s** — the live charts feed, keep it tight) and
  `Api__DomainPollMs` (default **5s**, relaxed — each tick spawns `kgsm.sh` and the roster changes
  rarely; commands push an immediate verify patch off the command path, so this only catches
  out-of-band changes). `LeafHealthMonitor` is **always-on** (~2s) — the single source feeding both this
  stream's `capabilities.patch` and the REST `GET /hosts` capability block, so they can't disagree.

## Auth

`/stream` is `[Authorize]` — any authenticated caller connects, and **the gate is per topic and per
frame** (`StreamProtocol.Gate`, `StreamAccess`): a host topic takes the action its REST companion does
(`api:logs.read`, `api:services.read`, `api:audit.read`, `api:alerts.read`, `api:batches.read`,
`api:hosts.read`, `monitor:metrics.read`); one server's metrics or console takes `kgsm:server.read` /
`kgsm:server.console.read` at that server; the server collections (`servers`, `servers/metrics`,
`jobs`, `players`) are open to subscribe and each frame reaches only the readers of the server it is
about (`Publish(..., serverId)`, `PublishRows`); `me` needs nothing; a topic this build does not know is
an Owner's alone. A topic the reader may not see **delivers nothing** — never a 403 on the whole stream.
What the open `me` buys is the one caller nothing else has anything to say to: somebody awaiting
approval, who connects to hear about their own account and hears nothing else.

The audit feed says the same things to every reader of it; only the values inside a row differ. A row
the engine classifies as carrying personal or privileged values is published with a redacted variant
(`StreamRedaction`), sent to readers without `api:audit.personal-fields`.

Fetch-based SSE sends the bearer as a normal `Authorization: Bearer` header through the standard
JwtBearer pipeline — a query-string token authenticates nothing (regression-pinned:
`Stream_Sse_QueryTokenIgnored`). SSE exposes a **readable `401`**, so the stream heals through the
same reactive rotate-on-401 path as every REST call — **don't introduce client-side expiry math for
this endpoint.**

**The connection re-checks its own session every 20s.** `[Authorize]` gates the CONNECT and nothing in
the framework re-runs it on a request that lasts hours, so `StreamController` hands the connection a
probe over `ClusterSessionRevocations` and the write loop ends the stream once the `sid` has been ended —
a revoke reaches the live channel in ≤20s, the same order as REST's ≤5s. Two things about it are
load-bearing: it runs on the **loop's own clock**, not inside the heartbeat branch (a busy stream is
woken by frames faster than any delay completes, so a duty hung off that branch never fires on the
connections carrying the most data), and it checks the **session, not the token's `exp`** — tearing a
stream down when the access token lapses would churn every client four times an hour and surface a
reconnect banner each time, for a credential the client is about to rotate anyway. A check that
THROWS ends the stream too: "couldn't measure" is not "still valid", and the redial re-runs the full
auth pipeline, which is the authority. No `sid` (auth-disabled) → no probe, unchanged behaviour.

**Access is evaluated per frame, and the evaluator is replaced whenever the replica moves.** The
connection keeps the topics the client asked for and asks on every frame whether its reader reaches
them, so access granted or taken away applies to the next frame, both ways, with no reconnect. Every
change the replica takes reaches every connection at once (`StreamAccessRefresh`, an
`IAuthorityChangeListener`, calls `StreamHub.AccessChanged`), and a reader whose account status moved
is sent a `me.patch`. The loop also re-reads the replica on the same 20s clock as the session check —
the backstop for a change that reached the file without this process being told. An unreadable replica
**ends the stream** rather than refusing everything: "we could not ask" is a third answer, and
flattening it into a refusal would report an outage as everybody having lost their access. The redial
re-runs the full auth pipeline, which is the authority.
