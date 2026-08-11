# CLAUDE.md — Services/Audit/

The append-only **audit log** (M5, architecture.html §3·d) — persistence *downstream* of the stateless
engine (keystone O3). `GET /api/v1/audit` (keyset) + the `audit` WS topic (`audit.append`). Built; the
contract is frozen in `PLAN.md §6` (audit row) + `§8` (M5 log). This file is the local "what you must not break."

## Locked decisions (do not relitigate)

- **Event-sourced, single writer, NO double-write.** kgsm **owns** `server.*`/`backup.*`. `AuditService`
  is the one writer; engine actions land via `KgsmAuditConsumer` (the kgsm-lib `IEventService` echo) —
  the API **never** writes an audit row when it *issues* a command. The command path only **stamps**
  `actor`+`origin` onto `ILifecycleService.*(serverId, actor, origin)` (kgsm-lib 1.8.0) so they ride the
  event. `auth.*` has no kgsm event → written directly (no double-write risk). **Never add a second
  writer for an action kgsm already emits** — you can't dedup echoes, which is the whole reason provenance
  is stamped instead.
- **actor and origin are independent axes.** `actor` = who (identity); `origin` = the surface. **Never
  derive origin from actor.** A missing/unknown origin is **`null`**, never a fabricated surface (the
  never-fabricate invariant). The command path's origin is **caller-declared** (`ui|assistant|discord|api`,
  default `api`; `system` is reserved for autonomous engine actions and rejected at the controller).
- **Append-only & immutable.** Rows are never updated or deleted; a correction is a *new* row. Don't add
  an update/delete path. `EnsureCreated`, **not an EF migration** (dev authority — wipe the DB on a schema
  change; see the api `CLAUDE.md` gotcha).
- **Closed `action` vocabulary** (`Contracts/AuditAction`). Clients/the model can't invent one. Add an
  action only when its producer has landed, never speculatively. `server.crash` covers the watchdog's
  `instance_crashed`→warn / `instance_failed`→danger (both `system`-stamped); `network.ports.open`/
  `network.ports.close` are the `instance_ports_opened`/`_closed` echoes. `network.ports.close` is a
  deliberate **server-side additive** action beyond the doc's `ports.open`-only `network` set — it is
  honestly sourceable and keeps the trail symmetric (a standalone `files firewall disable` would otherwise
  leave an opened-never-closed gap); the frontend accepts unknown actions forward-compat.
  `config.*`/`player.*`/`host.*`/… stay deferred. **The whole `network.*` set is engine-echo-only** — an
  instance's ports and router forwards are opened by the supervisor when it starts and released when it
  stops, so the api issues no network command and has nothing to direct-write. The per-event mapping
  policy lives in the **pure** `AuditMapping.From{Crash,Failed,PortsOpened,PortsClosed,UpnpOpened,
  UpnpClosed,UpnpReasserted}Event` mappers, unit-tested without a socket.
- **`network.upnp.reassert` is the one `network.*` action at `warn`.** It records that the watchdog's
  sweep found the ROUTER had dropped a running instance's forwards and put them back — a fact about the
  router, not about anything this host did, and the only signal an operator has that their IGD discards
  mappings it accepted (it can report a lease as infinite and drop it anyway). The open/close pair are
  `info` because they are the healthy lifetime; this one is an unhealthy condition being papered over, and
  a run of them is worth noticing. `meta.ports` carries **only the subset that was missing**, so a partial
  loss never reads as the whole set having gone.
- **What is a step and what is the news is the engine's answer, not a list here.** `EngineEventShaping`
  shapes nothing for a type `KgsmEventCatalog` classifies `Phase` — the brackets kgsm puts around an
  install, a stop, an update. Those are live state (they claim and release the in-flight job slot), and
  the fact worth an append-only row is the one in the middle. **Do not reintroduce a local skip-list**:
  it drifts silently, and the version that did missed the whole install bracket, so an install wrote a
  dozen `engine.instance_files_created` rows beside itself. The live path stays in step structurally —
  `KgsmAuditConsumer` publishes only for the types it maps, and the phase types it registers publish
  nothing.
- **`server.ready` is its own action, not a refinement of `server.start`.** The engine emits both:
  `instance_started` says the process spawned, `instance_ready` says the watchdog's log-scrape confirms
  the game will accept a connection. On a big world the two are minutes apart, and the second is what
  somebody asking "when could people actually get in" is looking for. Both halves write it — the read
  path maps it, the live handler publishes it beside closing the starting latch.
- **`origin` nullable** is a recorded §6 divergence, and so is **`meta.jobId`**: no id round-trips the
  stateless engine, and every action that reaches the audit log is an engine echo, so nothing here can
  populate it. Keep that limit in mind for the alert↔audit `resolution.actionId` bridge.

## Invariants when you touch this

- **Serialized writes.** `AuditService` holds a `SemaphoreSlim` write gate (SQLite is single-writer) and
  its **own DI scope per write** (writes arrive off the request path — the event listener). Don't capture
  a request-scoped `AppDbContext`. Reads (`AuditController`) use the request scope directly via `AuditQueries`.
- **Keyset pagination on `rowid`**, newest first (`RowId < cursor` `DESC`). Never offset pagination (it
  skips/repeats as the head grows). `nextCursor` only when the page came back full.
- **WS coalesce key = the unique event id** (`StreamProtocol.AuditEntityKey`), NOT a static `"audit"` key:
  audit appends are distinct facts and must never supersede one another in a slow client's queue. (Contrast
  the metric/status patches, which *are* supersede-by-latest.)
- **Actor parse fidelity** (`AuditMapping.ParseActor`): `provider:name` → `{kind,name,provider}`, kind
  derived from provider (`discord`→user, `api`→token, `system`→system); bare string = the OS-user fallback;
  unknown provider keeps the name but leaves provider `null` (never coerce). The load-bearing test is the
  **round-trip** (`discord:haru` → `{user,haru,discord}`) — keep it green when you touch the parser.
- **kgsm-lib only / the journal is shared and read-only.** Events come through `IEventService`, never a
  raw file read. `Api__KgsmJournalDir` names a directory the engine writes and every consumer reads —
  the API owns nothing there and nothing on the engine side needs configuring for it to arrive.
- **The API starts at the TAIL and keeps no cursor.** It never *persists* an engine event: it shapes each
  one into a live audit row, fans it out over SSE, and hands it to the notification bus. Replaying
  history on restart would re-announce to Discord/Slack events that were already announced. Nothing is
  lost by skipping the events emitted during a restart, because **the journal is the record** and
  `GET /audit` reads them back from it. **Do not give this consumer a cursor** without first moving the
  notification publish behind something that can tell a replay from a new event.
- **Engine history is read from the journal, never from a leaf.** `AuditQueries` takes kgsm-lib's
  `IEventJournalHistory`; the merge is local API-only rows ∪ the journal's shaped engine rows. This is
  what makes the audit trail complete on a host with no optional leaves installed, and it is the reason
  `engineHistoryDegraded` now means "unreadable journal or no engine" rather than "a leaf is down".
  **Resolve the reader from the request scope, not the constructor** — kgsm-lib registers only when the
  engine is provisioned, so a constructor parameter turns an engine-less host into a 500 on the one
  endpoint that explains what happened.
- **An engine event's id is its journal position** (`AuditId.ForPosition`, `evt_<segment>_<offset>`), not
  a hash of its contents. Content hashing could not separate two identical events inside one second —
  the engine's timestamps have one-second granularity — so the merge's dedup dropped the second one.
  The id also sorts like the journal, which is why one `(ts, id)` cursor still spans both merge sources.
  `EngineEventIdTracker` must keep deriving the live-push id from the same position, or a client
  reconciling an SSE row against `GET /audit` sees one fact under two ids.

## Degrade gracefully (don't crash startup)

`KgsmAuditConsumer.StartAsync` always `EnsureCreated`s (so `GET /audit` + auth writes work with no engine),
then wires events **only if** the engine is provisioned and `IEventService` resolves; an unreadable or
absent journal is logged and skipped (the read loop is kgsm-lib's own fire-and-forget task, and it
tolerates a directory that does not exist yet, so it never throws here). An auth audit
write is best-effort — a failed write must never break login. **Honest boundary:** events emitted while the
API isn't listening are **not** audited (stateless engine, no backfill) — state it, don't try to backfill.

## Auth

`GET /audit` is `[Authorize(Policy = viewer)]` — a core read surface ("every 'what happened' view reads
here"). The `audit` SSE topic rides the same viewer-gated `/stream` connection.

**The gate is on the feed; a few values inside it are on a second one.** `AuditRedaction` takes the
personal and privileged fields off a row for a reader below operator — a player's connection address,
what somebody typed at a console, who a moderation action named. Three rules hold it together:

- **Which values those are is the engine's classification, not this API's.** `KgsmEventCatalog`
  (kgsm-lib) says what each payload field holds; the Control Panel turns "not public" into "operator
  and above". A field reclassified upstream changes what a viewer sees with no edit here, and a field
  name is classified the same way on every event that carries it, which is what makes the lookup on a
  shaped row sound.
- **The row is never withheld — only values on it.** Every reader sees that a ban happened and that a
  command was run, with the same id, timestamp and actor. A shorter feed for one tier would be two
  people reading one host's history and being told different things.
- **The summary counts as a value.** Two actions print a restricted value in their own sentence
  (`console.input`, the moderation trio), so the redactor rebuilds those through `AuditMapping`'s own
  summary builders — the same call the mapper makes for an event that carried no such value, so the
  two cannot word one row differently.

**The live topic answers the same way.** `AuditService` publishes both shapes and `StreamHub` picks per
connection (`StreamConnection.IsOperator`, fixed at connect like the subscriptions), because a value
withheld on refresh and pushed live is the same value published, with a delay.
