# CLAUDE.md — tests/Api.Tests/

The API's test project — **xUnit + `WebApplicationFactory`**. It boots the **real** pipeline
in-process and replaces only the external dependency at the seam. Run with `dotnet test kgsm-api.slnx` (or this `.csproj`). net10, not under the
Api's `TreatWarningsAsErrors`.

**This suite runs on the host it tests against, so no test host may reach a live leaf.** `NoLiveWatchdog`
points `Api__WatchdogSocketPath` at a path that cannot exist for the whole run, beneath any fixture's own
setting. A host that dialed the real watchdog from its background services once exhausted the daemon's
file descriptors and took running game servers down; a new always-on client for another leaf needs the
same treatment before the suite runs on a live machine.

## How it works (the pattern to follow)

- **`AuthTestFactory : WebApplicationFactory<Program>`** boots the real app with **auth ON**, the
  cluster's auth anchor stood in for by a signer (`AuthTestFactory.AnchorSigner`, one for the whole
  run), and the **engine/monitor left unprovisioned** so reads degrade to `200` with no external
  dependency. It replaces `IClusterSessionKeys` with `PublishedAnchor.Default` — what gossip would have
  delivered — so the JwtBearer pipeline verifies the stand-in anchor's sessions exactly as production
  verifies the real one's.
- **Sessions are minted as the anchor mints them** — `factory.AccessToken(tier)` and
  `factory.RefreshToken(tier)` sign with the stand-in anchor's `SessionTokenService`, audienced to
  `AuthTestFactory.ClusterId`. A factory built with `WithWebHostBuilder` has its own replica, so mint for
  it with `AuthTestFactory.MintAccessOn(derived.Services, tier)`, which writes the account where that
  factory's requests will read it. The two refusal shapes a node must hold are in `TestTokens`: a session
  signed by a key nobody published, a symmetric token, and an anchor-signed one with no `sid`.
  `AccessToken` mints for the **one standing identity** (`FakeDiscordResolver.Identity`), so every token
  it hands out is the same person holding whichever tier was asked for last. A case about **who**
  something reaches, or about one account changing while another watches, needs two people:
  `FakeDiscordResolver.IdentityFor(subject)` names one and `factory.AccessTokenFor(identity, tier,
  status)` gives them a session and an account of their own.
- **A token's tier is a label; the account decides.** Every gate resolves authority from the replica
  per request, so minting at a tier proves nothing on its own — `AccessToken`/`AccessTokenFor` set the
  account to match. The node itself never writes an account, so a test plays replication's part:
  `AuthTestFactory.ReplicaOf(services)` opens the same file directly, and a change that must reach open
  streams goes through the registered `account.changed` handler (`MeStreamTests.Retier`), as the bus
  would deliver it.
- **`/api/v1/stream` (fetch-based SSE; protocol: `../../src/Api/Realtime/CLAUDE.md`)** is
  exercised with `SseTestHelpers.OpenStream(client, path, token)` — a `GET` with
  `HttpCompletionOption.ResponseHeadersRead` and an `Authorization: Bearer` header (never a query-string
  token; the server ignores query tokens and a dedicated regression test, `Stream_Sse_QueryTokenIgnored`,
  locks it). For tests that need to read frames off an open connection, wrap the response in
  `SseTestHelpers.Frames(resp)` → an `SseFrameReader` whose `WaitForFrame(predicate, timeout)` polls
  `data:` blocks (skipping `:`-comment-only ones like `connected`/`keepalive`) until one matches or the
  timeout elapses. There is no client→server channel — topics are baked into the connect URL,
  immutable per connection.

## Two fakes that are FILES, not dictionaries

Most seams here are faked in memory. Two are not, because the bug class they guard only exists on
disk, and a suite that records into a dictionary cannot see it:

- **`ServerNoteRoundTripTests`** gives its fake `IInstanceService` a real `.config.ini` and reads it
  back **by sourcing it in bash** — the way kgsm does. A note body carrying `"`, `$`, a backtick or a
  newline is inert only because it is base64; unencoded, bash expands it and the note silently becomes
  a different sentence (the suite's last test writes one raw to demonstrate exactly that).
  **That read is a real `source`, so fixture data here is executable code whenever the encoding is
  out of the path** — which a mutation check proving the encoding matters will do deliberately. Three
  things keep it inert and all three are load-bearing: the bodies name only commands that change
  nothing, `SourceKey` runs bash with an **empty `PATH`** so no external binary resolves (`source` and
  `printf` are builtins; `HOME` stays set for the expansion assertion), and
  `FixtureBodiesNameNoDestructiveCommand` fails when a destructive verb enters the bodies. Don't drop
  the `PATH` line as dead code.
- **`AuditJournalRelayTests`** points `Api__KgsmJournalDir` at a temp directory and appends real NDJSON
  lines, so kgsm-lib's journal reader → `KgsmAuditConsumer` → the `audit` SSE topic runs end to end.
  It also locks the other half: an engine-sourced event is published live but **never persisted** as a
  local row (kgsm-monitor owns that history). Write the segment with a **BOM-less** UTF-8 encoding —
  `Encoding.UTF8` emits one when it creates the file, and the first line then starts `0xEF` and is
  dropped as unparseable.

Both use temp fixtures on purpose. The engine's real journal is one shared host-wide file that every
kgsm-api on the box reads, so a test writing to it would land permanently in the operator's audit log —
the same rule that keeps kgsm-web's smoke read-only (`kgsm-web/CLAUDE.md`).

## Setting connection facts a test server does not supply

`ForwardedHeadersTests` needs a **remote IP address**, because the forwarded-headers trust decision
turns on who the immediate peer was — and an in-memory test server leaves it null, which reads as
"not a trusted proxy" and would make every such test pass for the wrong reason. It stamps one with an
`IStartupFilter` registered through `ConfigureTestServices`: a filter's middleware runs **before** the
app's own `Configure`, which is the only way to get ahead of `UseForwardedHeaders` from outside the
pipeline. Use the same trick for any other `HttpContext.Connection` fact a test needs to control.

## What lives here vs. smoke

- **Here:** behavior that needs in-process service replacement or deterministic control — the auth
  **401/403/tier matrix** against anchor-signed sessions, the ended-session deny-list, a replicated
  change reaching an open stream. `401` (no/invalid bearer) vs `403` (authenticated, tier too low) is
  the load-bearing split — assert both.
- **`scripts/smoke.sh`:** the HTTP **contract surface** end-to-end (envelopes, DTO shapes, the SSE
  stream protocol, the no-token sweep) against a real running process. The two are complementary, not
  redundant.

## Convention for new tests

*Behavioral* tests land here, faking the relevant boundary (the leaf client, the event socket, the
auth anchor); smoke keeps proving the wire contract. Keep fakes switch-on-input rather than mutable,
so tests stay parallel-safe.
