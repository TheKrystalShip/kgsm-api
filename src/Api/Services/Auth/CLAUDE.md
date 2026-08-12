# CLAUDE.md — Services/Auth/

Auth — **per-host, Model A** (`architecture.html §3·f`, keystone O5). **Identity** is proved either by
a KGSM password or by an external provider used as an SSO anchor; **authority** is the tier on the
caller's KGSM account, read from the host's shared account store. The bearer is a short-lived
host-scoped JWT this host mints after verifying identity once. The authority for the contract is
`PLAN.md §6` (auth row) + `§8`, and for the identity model `../auth-internal-users-plan.md`. This
file is the local "what you must not break."

## Locked decisions (do not relitigate)

- **Bearer = HMAC-SHA256 JWT.** Access ~15 min + refresh with a **30-day sliding window**: a session's
  `Expires` is `now + 30d` at login and slides to `now + 30d` on each successful refresh — a session used
  ≥once inside the window stays alive; an idle one dies 30d after its last use. The ~15-min access TTL bounds
  privilege. ⚠ The multi-week refresh only survives if `Api__SigningKey` is **stable** — an ephemeral
  per-process key invalidates every token on restart (the ctor logs a warning).
- **The session registry is the revocation authority; the hot path stays cached.** A `SessionEntry` table
  (`Data/SessionEntry.cs` + `Services/Auth/SessionStore.cs`, on `AppDbContext` via `EnsureCreated`; UTC-ticks
  `ValueConverter`s so `Expires > now` is a translatable indexed `INTEGER` compare) holds one row per
  (login × device). Every request carries a stable **`sid`** JWT claim checked against the registry, **cached in
  `IMemoryCache` (5s TTL, evicted on revoke)** — never a per-request DB hit; the check is wired into the JwtBearer
  `OnTokenValidated` so REST + SSE both honor it. **No user-profile row** — identity is the login-time JWT-claim
  snapshot; there is no `UserProfile` entity and no display-name store, and adding one is out of bounds (§3·f
  "no user row anywhere" bars profile state that follows a user across devices; a session row is revocation
  state, not that).
- **Every refresh rotates both tokens; reuse is detected.** `/auth/session/refresh` mints a fresh access **and**
  refresh token; a per-token `jti` claim + the row's `CurrentJti` detect reuse — a stale/replayed refresh token
  (`jti` ≠ `CurrentJti`) → `401`. `RefreshResponse` carries a **`refresh`** field; a client MUST adopt both
  tokens on each call (the old refresh token is dead).
- **Revocation is ≤5s; the live access token is a ≤15-min hard ceiling.** A revoke (logout / self-revoke /
  admin cross-user) soft-deletes the row (`Revoked=true`) and evicts the cache → effective within ≤5s (the cache
  TTL backstop; ~instant on the same node via `Evict`). The access token is not re-validated mid-life beyond the
  `sid` check the 5s cache bounds, so it stays valid up to its ~15-min TTL. **An already-open SSE stream is
  covered too** — it re-checks its own `sid` every 20s, so a revoke cuts the live channel within ≤20s
  rather than leaving it pushing until the tab closes. Expired/revoked rows are hard-deleted
  by the 10-min `SessionCleanupWorker`. A token with no `sid` claim → `401` (no grandfathering).
  `Api__SessionsDisabled=true` makes the whole registry inert — no per-request check, no revoke surface, no GC
  (the stateless-JWT escape hatch).
- **`ISignInService` is the seam the login path depends on**, and it lives in
  `TheKrystalShip.KGSM.Auth`, shared with every other KGSM surface. It composes two halves registered
  separately here, and **they come from two different places**: `IIdentityProvider` is
  `DiscordDirectory` (who someone is, and the one chokepoint to `discord.com` — **never** call
  `discord.com` from anywhere else), and `IAuthorityProvider` is the account store (what they may
  do). A guild role is a fact about a chat server, not about this host, and nothing here reads one.
  The seam is what makes the whole 401/403/tier matrix testable in-process with a fake
  (`tests/Api.Tests`) and keeps two surfaces from resolving the same person differently.
  ⚠ The Discord registrations stay **transient**, matching the typed `HttpClient` underneath. A
  singleton would pin one handler for the process lifetime and stop `HttpClientFactory` rotating it;
  `AuthServiceGraphTests.TheSignInGraphIsTransient` is what holds that line. The authority half
  resolves from the singleton `UserDirectory` — one file, one cache — through `DirectoryAuthority`,
  which exists so the seam is still *resolvable* when the store will not open: a service that cannot
  be constructed takes down the endpoints whose job is to report the problem.
- **An identity names its provider.** `KgsmIdentity.Handle` (`provider:subject`) is the token subject,
  the session-row key and the `userId` on the wire — built by the identity, never interpolated at a
  call site. For a Discord login it is the same `discord:<id>` string it has always been, so live
  sessions and stored rows are unaffected by the provider becoming explicit.
- **The session machinery is the ecosystem's too.** `ISessionTokenService`, `SessionValidator`,
  `SessionCleanupWorker`, `ISessionRegistry` and the claim readers come from
  `TheKrystalShip.KGSM.Auth.Sessions`. `SessionStore` stays here — it IS this API's `ISessionRegistry`
  (EF/SQLite, beside the audit log) and keeps a richer surface on top for the admin endpoints.
  ⚠ **`Issuer` stays `"kgsm-api"`** (`ApiOptions.ToSessionTokenOptions`): it is validated, so adopting
  the package's neutral default would 401 every token already issued and log everyone out.
  ⚠ **`SessionsRefreshAbsoluteDays` is the only session lifetime.** The token's expiry and the
  registry row's are both derived from it; never reintroduce a constant beside it.
- **`Api__SessionsDisabled` is composed, not branched.** Startup registers `InertSessionValidator` and
  no GC worker instead of teaching the shared types about a flag only this surface has.
- **The tier vocabulary is the ecosystem's.** `KgsmTier`/`KgsmTiers`/`KgsmAuthClaims`/`KgsmTokenKind`
  come from `TheKrystalShip.KGSM.Auth`; this project keeps only `AuthPolicy` (ASP.NET policy names)
  and `TierAuthorizationHandler` (how this surface enforces them). There is no local tier enum to
  drift.
- **Authority is resolved per request, from the account store, and the `tier` claim is not trusted.**
  `LiveAuthority` runs on the JwtBearer `OnTokenValidated` event and replaces the minted claim with
  what the account says today, so disable, demote and revoke are one mechanism: change the record,
  and the next request reads the record. The claim stays on the token as a display hint the SPA can
  render before its first call. `Api__AuthorityCacheSeconds` (default 5) is the only staleness left
  in the model, and is therefore the demotion lag.
  ⚠ Three outcomes, kept apart: a **disabled** account fails authentication (its live sessions end);
  an identity with **no account** is a stranger holding `none` (a real answer — the session stands and
  every gate refuses it); an **unreadable store** answers `502 authority_unavailable` via
  `OnChallenge` — never a `401`, which would send a browser to a sign-in that reads the same file, and
  never the token's own tier, which would let a demoted admin stay one for the length of the outage.
- **Signing in needs the application and this host's callback, and nothing else.** The Discord
  application is **shared external config** (the same one the host's bot and the assistant use) —
  **NOT a process dependency on kgsm-bot** (keystone §4); hold our own copy in config and never reach
  into the bot. The guild, the bot token and the role ids in that same shared file are kgsm-bot's:
  they bind to nothing here, and describing one on this leaf's configuration page would offer an
  operator a knob that changes nothing.
- **Connecting a provider account is self-service, and both writes need the credential proved again**
  (`IdentitiesController`, `ReauthGate`). A link outlives the session that makes it — afterwards,
  whoever holds that provider account can sign in as this one — and a live session can be a borrowed
  unlocked laptop, so it asks. Signing in stamps the session it mints, so the common path is never
  prompted. The start is an XHR returning a URL (a bearer does not survive a top-level navigation)
  and hands the browser a one-time ticket cookie; the account being changed stays server-side in
  `LinkTicketStore`, because a cookie is a value the browser holds and the browser is not the
  authority on whose account this is. Detaching revokes the sessions that identity established, and
  the last credential is refused.
  ⚠ **A link runs on its own redirect URI** (`ApiOptions.DiscordLinkRedirectUri`, derived from the
  login one so the two cannot name different origins) and Discord accepts only registered URIs — so
  `/auth/identities/discord/callback` must be registered on the application beside the login
  callback. Both flows use the same `DiscordDirectory`, keyed apart at composition, because the
  redirect sent at the bounce and at the exchange must match.
- **A verified identity with no account here is provisioned, not denied.** It gets an unapproved
  account and a real session holding `none`, so a surface can say "awaiting approval" rather than
  showing somebody who just proved who they are a bare `403`. That is an unauthenticated write
  surface, so it is capped (`Api__PendingUserCap`) with an expiry (`Api__PendingUserTtlDays`) that
  only ever removes an account which arrived this way, is still unapproved, and has no password.
  The terminal `403` is now a fact about the account (switched off), never about a guild.
- **Auth is ON by default.** `Api__AuthDisabled=true` swaps in `DisabledAuthHandler` (synthetic
  admin — the pre-M4 open window), loudly logged. Never enable it on an exposed host.

## Invariants when you touch this

- **Secure-by-default.** A `FallbackPolicy` requires an authenticated caller, so a **new endpoint is
  gated unless it opts out**. Adding an open endpoint is a deliberate, reviewed act — not an omission.
  Three carry `[AllowAnonymous]`: `/health` + `/api/v1` (pre-login reachability), and
  `POST /notifications/actions/{handle}`, which is the **one anonymous write** and needs its own
  paragraph. A service worker holds no session — it can read neither the access token in
  `sessionStorage` nor the refresh token in `localStorage` — so a notification button has no bearer to
  present and the handle stands in for one. What keeps that sound is that the handle names an operation
  **staged server-side** (the assistant's model, so a request describes nothing and can poison nothing),
  is **bound to the push endpoint** it was staged for, is **single-use with a short life**, and — the
  load-bearing one — **resolves the tier at redemption from the account store**, never from anything
  carried since staging. Someone demoted or switched off between the notification and the tap is
  refused. Every refusal about the handle is one `404` with one message, because separate answers let
  somebody probe which handles exist.
- **Tier gating** (hierarchical: admin ⊇ operator ⊇ viewer): viewer = reads + the `/stream` WS,
  operator = the command `POST`, admin = diagnostics + reserved (settings/install/audit-config).
  `401` = no/invalid bearer (challenge); `403` = authenticated, tier too low (forbid) — keep that split.
- **Honest failure modes** (the security analog of never-fabricate-a-status): the identity provider
  unreachable → `KgsmAuthProviderException` → `502`; the account store unreadable →
  `502 authority_unavailable`. **Never a default grant, and never a denial either** — "we could not
  ask" is a third answer and must stay one. A disabled account → terminal `403`; a tier is **never**
  silently softened to make a request work.
- **A refresh token is never an access bearer.** `OnTokenValidated` rejects `tkn != "access"` on
  protected calls; only `/auth/session/refresh` reads a refresh token (from the `Authorization` header).
- **The SSE bearer is a normal `Authorization` header**, so `/stream` authenticates through the standard
  JwtBearer pipeline like every other request — a query-string token authenticates nothing
  (`Stream_Sse_QueryTokenIgnored`). Because that pipeline runs **once per request** and a stream is one
  request lasting hours, the connection re-asks the registry whether its `sid` is still alive every 20s
  and ends itself when it isn't (`Realtime/StreamConnection.cs`). **The re-check is on the SESSION, not
  on the access token's expiry** — a token lapses every ~15 min by design and the client rotates it
  reactively, so ending a stream on that would churn every client four times an hour and cost the panel
  a visible reconnect each time. A live stream is never torn down for mid-stream token expiry.
- **`/auth/session` returns the login-time profile snapshot** embedded in the token claims, NOT a live
  re-fetch — the provider's token is discarded at callback, so "fetched live" can't hold (a §6
  divergence). The **tier** on `/me` is the exception and is live, because the authority resolution
  above has already replaced it on the principal.
- **A peer's cluster vouch carries identity only.** Its asserted tier is deliberately not read: the
  vouched identity resolves against *this* node's account store, so an admin on one node is not an
  admin on every node that trusts it. `ClusterSessionRequest.Tier` stays on the wire (renaming it is a
  cluster-wide version skew) and is ignored.

- **OAuth `state` CSRF (M4·b).** `/start` sets a one-time HttpOnly `state` cookie (`kgsm_oauth_state`);
  `/callback` requires the echoed `state` to equal the cookie (constant-time) *before* any Discord
  exchange, else `400 invalid_state`, then clears it (no replay). **Stateless** — a client cookie, no
  server store (honors the no-session-table decision). `SameSite=Lax` (NOT Strict — it must ride
  Discord's top-level redirect back), `Secure = Request.IsHttps` (off on http loopback, on under
  https), `Path=/auth/discord`. Don't switch it to a server-side pending-state store.

## M4 status — backend built & live-validated (2026-06-15)

The real HTTP identity provider is **live-validated** on the trusted host: a real Discord login
verified an identity, the account it proved decided the tier, the bearer was minted, and that bearer
passed live tier-gating end-to-end (PLAN.md §8 M4·b). The login endpoints `503` only until
the `Api__Discord*` settings are configured. **What's still owed for the *full* M4: only the frontend gate**
(the per-host session state machine + tier-gated controls — the SPA, still `planned`). Op note: dev ran an
**ephemeral** signing key (`Api__SigningKey` blank → tokens die on restart) — set a stable secret
on any real host. To run with the dev creds, the env must be `Development` (`ASPNETCORE_ENVIRONMENT=Development`)
or `kgsm-api.settings.Development.json` is ignored and the login endpoints `503` as if unconfigured.

## Session registry status — self-validated 2026-07-09; live OAuth soak owed

The registry + revocation surface (the four locked bullets above) is built and self-validated
(**655/655 tests, 0-warn**): the `SessionEntry` registry + cached validator, `sid` claim, sliding-window
refresh with `jti` rotation/reuse-detection, server-side logout, the revoke surface (`GET /auth/sessions`
viewer-self / admin `?userId=`; `POST /auth/session/revoke {sid?|all?}`; admin `POST /auth/sessions/{sid}/revoke`
+ `POST /auth/users/{userId}/sessions/revoke-all`), three `auth.session.*` audit actions, `/me.recentLogins`,
mint-time `expiresAt`, and the 10-min GC worker. Full record: `docs/session-management-plan.md`.
**Owed:** a live OAuth soak (a real Discord bounce leaving a real device UA in a row + a self-revoke-all
forcing a relogin). ⚠ **Deploy note:** a token with no `sid` claim `401`s, so at the first deploy of the
registry every pre-existing refresh token dies — announce a forced relogin. Fresh DBs get the `sessions` table
from `EnsureCreated`; an already-deployed DB needs the table + `CurrentJti` column created once in place (audit
rows untouched).
