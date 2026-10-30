# CLAUDE.md — Realtime/

The per-host realtime stream — `GET /api/v1/stream`, fetch-based SSE. The contract is
`../../../../kgsm-docs/reference/realtime-stream.md`; the system is
`../../../../kgsm-docs/systems/control-panel/realtime.md`. This file is the local "what you must not break."

## Locked decisions (do not relitigate)

- **Hand-rolled SSE, NOT SignalR.** The `{ topic, type, data }` envelope **is** the contract —
  SignalR's framing would break it. **Fetch-based SSE, not native `EventSource`**, which can't set an
  `Authorization` header (see Auth below). Subscriptions are **immutable per connection**: changing
  topics means opening another stream. Framing, topics and cadence:
  `../../../../kgsm-docs/reference/realtime-stream.md`.
- **All topic/type strings live in `StreamProtocol.cs` — never inline** (a standing user requirement).
  Add a new topic or message type there; a literal `"servers"` or `"metrics.tick"` anywhere else is a bug.
- **Patch-only, no snapshot-on-subscribe.** The client hydrates via REST and re-hydrates on
  (re)connect. Don't send a full snapshot when a client subscribes.
- **Coalesce-to-latest per key** is the backpressure rule: a slow client gets the *newest* frame, never
  an unbounded backlog; a stalled send is torn down and the client reconnects. Don't buffer history.
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
frame** (`StreamProtocol.Gate`, `StreamAccess`): a host topic takes the action its REST companion does;
one server's metrics or console takes `kgsm:server.read` / `kgsm:server.console.read` at that server; the
server collections (`servers`, `servers/metrics`, `jobs`, `players`) are open to subscribe and each frame
reaches only the readers of the server it is about (`Publish(..., serverId)`, `PublishRows`); `me` needs
nothing, so somebody awaiting approval hears about their own account and nothing else; a topic this
build does not know is an Owner's alone. A topic the reader may not see **delivers nothing** — never a
403 on the whole stream. The per-topic actions are `StreamProtocol.Gate`; the model is
`../../../../kgsm-docs/systems/control-panel/realtime.md`.

The audit feed says the same things to every reader of it; only the values inside a row differ. A row
the engine classifies as carrying personal or privileged values is published with a redacted variant
(`StreamRedaction`), sent to readers without `api:audit.personal-fields`.

Fetch-based SSE sends the bearer as a normal `Authorization: Bearer` header through the standard
JwtBearer pipeline — a query-string token authenticates nothing (regression-pinned:
`Stream_Sse_QueryTokenIgnored`). SSE exposes a **readable `401`**, so the stream heals through the
same reactive rotate-on-401 path as every REST call — **don't introduce client-side expiry math for
this endpoint.**

**The stream ends when the caller's access token expires.** `[Authorize]` gates the CONNECT and nothing in
the framework re-runs it on a request that lasts hours, so `StreamController` runs the connection on
`KgsmPerson.UntilExpiry(HttpContext)`: the request's cancellation, cut short at the token's `exp`. The
client reconnects with a fresh token, and the redial re-runs the full auth pipeline, which is the
authority. A revoked session and a changed role therefore reach the live channel within one token
lifetime, the same bound REST has. A member acting as itself and an auth-disabled host carry no person,
so their streams end with the request alone.

**Access is evaluated per frame, from the actions the token listed.** The connection keeps the topics
the client asked for and asks on every frame whether its reader reaches them (`StreamAccess`, fixed for
the connection's life). A frame about one person (`StreamHub.PublishToAccount`) reaches only the
connections whose token subject is that person.
