# CLAUDE.md — tests/Api.Tests/

The API's test project — **xUnit + `WebApplicationFactory`**, stood up at **M4·a** (auth was the
milestone that justified it). It boots the **real** pipeline in-process and replaces only the external
dependency at the seam. Run with `dotnet test kgsm-api.slnx` (or this `.csproj`). net10, not under the
Api's `TreatWarningsAsErrors`.

## How it works (the pattern to follow)

- **`AuthTestFactory : WebApplicationFactory<Program>`** boots the real app with **auth ON** + a known
  `KGSM_API_AUTH_SIGNING_KEY`, the Discord config present (so the login path runs), and the
  **engine/monitor left unprovisioned** so reads degrade to `200` with no external dependency.
- **`FakeDiscordResolver`** replaces `IDiscordIdentityResolver` (via `ConfigureTestServices` +
  `RemoveAll`). It is **the seam that makes auth testable without `discord.com`** — the M3
  "exercise the contract without the live dependency" move. It **switches purely on the OAuth `code`**
  (`viewer`/`operator`/`admin`/`none`/`bad`/`boom`), so cases are stateless and parallel-safe. No shared
  mutable state, no test ordering.
- **Mint tokens via the server's OWN token service** — `factory.AccessToken(tier)` /
  `RefreshToken(tier)` resolve `ISessionTokenService` from `factory.Services`, so the key + host audience
  match the running pipeline. For a deliberately-wrong-signature token, `TestTokens.MintAccessWithKey`.
- **`/api/v1/stream` (fetch-based SSE since the 2026-07-02 migration, `sse-migration-plan.md`)** is
  exercised with `SseTestHelpers.OpenStream(client, path, token)` — a `GET` with
  `HttpCompletionOption.ResponseHeadersRead` and an `Authorization: Bearer` header (never a query-string
  token; that hack is gone server-side and a dedicated regression test, `Stream_Sse_QueryTokenIgnored`,
  locks it). For tests that need to read frames off an open connection, wrap the response in
  `SseTestHelpers.Frames(resp)` → an `SseFrameReader` whose `WaitForFrame(predicate, timeout)` polls
  `data:` blocks (skipping `:`-comment-only ones like `connected`/`keepalive`) until one matches or the
  timeout elapses — the SSE-era analogue of the old WS `Send`/`Receive` pair, minus `Send` (topics are
  baked into the connect URL now, immutable per connection).

## What lives here vs. smoke

- **Here:** behavior that needs in-process service replacement or deterministic control — the auth
  **401/403/tier matrix**, the callback verdict (ok/denied/invalid/upstream-error), refresh rotation,
  the session snapshot. `401` (no/invalid bearer) vs `403` (authenticated, tier too low) is the
  load-bearing split — assert both.
- **`scripts/smoke.sh`:** the HTTP **contract surface** end-to-end (envelopes, DTO shapes, the SSE
  stream protocol, the no-token sweep) against a real running process. The two are complementary, not
  redundant.

## Convention for future milestones

Each milestone's *behavioral* tests land here, faking the relevant boundary (the leaf client, the
event socket, the Discord seam); smoke keeps proving the wire contract. M5 audit, M6 alerts, M7
assistant, etc. add their fakes + assertions alongside these. Keep fakes switch-on-input (like
`FakeDiscordResolver`) rather than mutable, so tests stay parallel-safe.
