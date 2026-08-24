# CLAUDE.md — Services/Audit/

The append-only **audit log** (architecture.html §3·d) — persistence *downstream* of the stateless
engine (keystone O3). `GET /api/v1/audit` (keyset) + the `audit` SSE topic (`audit.append`). The
contract is frozen in `PLAN.md §6` (audit row). This file is the local "what you must not break."

## Locked decisions (do not relitigate)

- **Event-sourced, and every producer records its own actions.** kgsm **owns** `server.*`/`backup.*`;
  the watchdog owns what it supervises; the monitor owns what it measured; and this API owns `auth.*`,
  `user.*`, `identity.*`, `service.*`, `file.write` and `backup.download`, which it writes to **its own
  journal** (`ApiJournal` → `Api__EventJournalDir`). `GET /audit` is the merge of every journal plus the
  local table's historical rows. **Nothing writes an audit row directly** — there is no append
  path on `AuditService`.
  The API **never** records a row when it *issues* a command to another component: the command path only
  **stamps** `actor`+`origin` onto `ILifecycleService.*(serverId, actor, origin)` so they ride that
  component's own event. **Never record an action another producer already emits** — you can't dedup
  echoes, which is the whole reason provenance is stamped instead.
- **This API tails its own journal, and that is what publishes its rows live.** `AddKgsmJournalFederation`
  reads every journal it discovers, and `/var/lib/kgsm-api` is one the scan matches — so writing is the
  whole of the write path, and `KgsmAuditConsumer.PublishOwnEventAsync` shapes and announces off the raw
  hook. ⚠ Announcing from the write site as well would emit every one of these rows **twice**. The raw
  hook rather than `RegisterHandler<T>` because kgsm-lib keys typed handlers on the payload CLASS, and
  several of these types deliberately share one (`auth_login`/`auth_logout` are the same shape).
- **The API's journal is NAMED to the federation, not left to the scan** (`namedJournals:` in `Startup`).
  Its path is configurable, and the scan only finds a producer at its default state directory — so a host
  pointing it elsewhere would have this API writing a record it could not read back.
- **actor and origin are independent axes.** `actor` = who (identity); `origin` = the surface. **Never
  derive origin from actor.** A missing/unknown origin is **`null`**, never a fabricated surface (the
  never-fabricate invariant). The command path's origin is **caller-declared**
  (`ui|assistant|discord|api`, default `api`); **two values are reserved and rejected at the controller**
  — `system` for autonomous engine actions, and `notification` for a push-notification button this API
  redeemed, since a caller naming either would be claiming to be something this API cannot check.
  ⚠ `AuditOrigin.IsKnown` is a **gate, not a display list**: `AuditMapping` normalizes an unrecognised
  origin to `null`, so a value stamped on an engine call but missing from that set comes back off the
  echo having lost its whole provenance, silently and at runtime.
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
  `host.threshold.breach`/`host.threshold.clear` are the metric-threshold pair — see below.
  `config.*`/`player.*`/… beyond those stay deferred. **The whole `network.*` set is engine-echo-only** — an
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
  the fact worth an append-only row is the one in the middle. **Do not add a local skip-list**:
  `KgsmEventCatalog` decides, and a local list drifts silently. The live path stays in step structurally —
  `KgsmAuditConsumer` publishes only for the types it maps, and the phase types it registers publish
  nothing.
- **`server.ready` is its own action, not a refinement of `server.start`.** The engine emits both:
  `instance_started` says the process spawned, `instance_ready` says the watchdog's log-scrape confirms
  the game will accept a connection. On a big world the two are minutes apart, and the second is what
  somebody asking "when could people actually get in" is looking for. Both halves write it — the read
  path maps it, the live handler publishes it beside closing the starting latch.
- **`host.threshold.*` comes from kgsm-monitor's own journal and names the monitor.** The monitor
  evaluates the threshold rules against every sample it takes and records the episodes it established.
  **Two actions, not one with a mutable state**: a breach and a recovery are separate immutable facts, and
  the live mutable view of the same condition is the alert feed. `episodeId` (carried in `meta`) is what
  pairs them.
  **The actor is `system:monitor`** — the ecosystem's autonomous-emitter form, the same one kgsm-watchdog
  stamps as `system:watchdog`. A bare `system` would make these indistinguishable from every other
  unattended action. The identity comes from the **journal the line was read from**, never from a field in
  the payload: a claim about identity made inside data is one this API cannot check. `origin` stays `system` — no surface drove it, and the closed origin vocabulary has no
  per-component value (the `auth.cluster_session` precedent: identity detail goes in the row, the
  vocabulary is not widened).
- **`command.*` is what this API observed and nobody else can.** kgsm emits an event when a verb
  *works*; a verb that fails or is refused exits non-zero and emits nothing, and a batch member
  cancelled in the queue never reaches the engine — so there is no echo to ride and no double-write
  risk. **Three actions, not one with the outcome in `meta`**: a fault to chase, a node that is full,
  and somebody calling off queued work are three different questions, and a refusal filed beside a
  failure blames the instance for the fleet being out of room. `command.refused` is keyed on kgsm's
  `EC_INSUFFICIENT_MEMORY` (51) via `EngineExit`, never on the engine's message. ⚠ **`update` and
  `uninstall` write nothing here** — kgsm emits `instance_update_failed`/`instance_uninstall_failed`,
  which are already mapped, and the exclusion lives in `CommandRunner.EngineRecordsItsOwnFailure`.
  ⚠ The meta key is **`verb`, never `command`**: `AuditRedaction` strips by field NAME across every
  event, and the engine classifies `Command` as privileged for the console-input event.
- **`origin` nullable** is a recorded §6 divergence, and so is **`meta.jobId`** on every echo-sourced row: no
  id round-trips the stateless engine, so a row shaped from another producer's event cannot name one.
  The rows this API writes itself are the exception — `command.*` is written by the process that owns
  the job, so carrying the id there reports what it holds rather than reconstructing anything. Keep the
  echo-side limit in mind for the alert↔audit `resolution.actionId` bridge.

## Invariants when you touch this

- **`AuditService` announces; it does not write.** It publishes to the realtime topic and the
  notification bus, and owns `EnsureCreated` for the table holding historical rows. Reads
  (`AuditController`) use the request scope directly via `AuditQueries`. Don't add an append
  path — a second writer for a fact a producer already records is undedupable.
- **Keyset pagination on a composite `(ts, id)`**, newest first, spanning the local table and every
  journal. Never offset pagination (it skips/repeats as the head grows). `nextCursor` only when the page
  came back full.
- **Every filter must be applied to BOTH halves of the merge.** `severity`, `actor` and `category` have no
  journal-side equivalent, so they narrow the EF query in SQL and the shaped records in memory — and the
  two must mean the same thing (`actor` matches the parsed actor NAME on both sides). ⚠ A filter
  implemented on only one half does not return a short page; it returns *everybody's* rows from the other
  half, which reads as a working filter until somebody checks.
- **Stream coalesce key = the unique event id** (`StreamProtocol.AuditEntityKey`), NOT a static `"audit"` key:
  audit appends are distinct facts and must never supersede one another in a slow client's queue. (Contrast
  the metric/status patches, which *are* supersede-by-latest.)
- **Actor parse fidelity** (`AuditMapping.ParseActor`): `provider:name` → `{kind,name,provider}`, kind
  derived from provider (`discord`→user, `api`→token, `system`→system); bare string = the OS-user fallback;
  unknown provider keeps the name but leaves provider `null` (never coerce). The load-bearing test is the
  **round-trip** (`discord:haru` → `{user,haru,discord}`) — keep it green when you touch the parser.
- **kgsm-lib only.** Events come through `IEventService`/`IEventJournalHistory`, never a raw file read,
  and this API's own journal is written through `IEventJournalWriter` for the same reason. Every journal
  but its own is **read-only** to it: `Api__KgsmJournalDir` and each leaf's state directory are written by
  their owners and read by every consumer, and nothing on their side needs configuring for it to arrive.
- **The API starts at the TAIL and keeps no cursor.** It never *persists* an engine event: it shapes each
  one into a live audit row, fans it out over SSE, and hands it to the notification bus. Replaying
  history on restart would re-announce to Discord/Slack events that were already announced. Nothing is
  lost by skipping the events emitted during a restart, because **the journal is the record** and
  `GET /audit` reads them back from it. **Do not give this consumer a cursor** without first moving the
  notification publish behind something that can tell a replay from a new event.
- **Engine history is read from the journal, never from a leaf.** `AuditQueries` takes kgsm-lib's
  `IEventJournalHistory`; the merge is local API-only rows ∪ the journal's shaped engine rows. This is
  what makes the audit trail complete on a host with no optional leaves installed, and it is why
  `engineHistoryDegraded` means "unreadable journal or no engine".
  **Resolve the reader from the request scope, not the constructor** — kgsm-lib registers only when the
  engine is provisioned, so a constructor parameter turns an engine-less host into a 500 on the one
  endpoint that explains what happened.
- **An event's id is its journal position** (`AuditId.ForPosition`, `evt_<producer>_<segment>_<offset>`), not
  a hash of its contents. A position-derived id keeps two identical events inside one second distinct —
  the engine's timestamps have one-second granularity, so a content hash cannot, and the merge's dedup
  would collapse them. The id also sorts like the journal, which is why one `(ts, id)` cursor still spans both merge sources.
  `EngineEventIdTracker` must keep deriving the live-push id from the same position, or a client
  reconciling an SSE row against `GET /audit` sees one fact under two ids.

## Degrade gracefully (don't crash startup)

`KgsmAuditConsumer.StartAsync` always `EnsureCreated`s and always creates this API's journal directory
(a reader discovers a producer by finding its journal, so one that has recorded nothing yet must still be
visible), then wires events **only if** the engine is provisioned and `IEventService` resolves; an unreadable or
absent journal is logged and skipped (the read loop is kgsm-lib's own fire-and-forget task, and it
tolerates a directory that does not exist yet, so it never throws here). Recording is best-effort throughout —
`ApiJournal` logs a failure and swallows it, because a sign-in that already happened must not turn into an
error because writing it down did not work. **Honest boundary:** events emitted while the
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
