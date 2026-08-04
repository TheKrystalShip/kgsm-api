# CLAUDE.md — Services/Auth/

Auth — **Discord per-host, Model A** (`architecture.html §3·f`, keystone O5). Identity is a
global Discord SSO anchor; **authorization is a short-lived host-scoped bearer** this host mints
after verifying identity once and resolving the role via the host's bot. Built at **M4·a**; the
authority for the contract is `PLAN.md §6` (auth row) + `§8` (M4·a log). This file is the local
"what you must not break."

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
  `sid` check the 5s cache bounds, so it stays valid up to its ~15-min TTL. Expired/revoked rows are hard-deleted
  by the 10-min `SessionCleanupWorker`. A token with no `sid` claim → `401` (no grandfathering).
  `Api__SessionsDisabled=true` makes the whole registry inert — no per-request check, no revoke surface, no GC
  (the stateless-JWT escape hatch).
- **`IDiscordIdentityResolver` is the seam — the one chokepoint to `discord.com`.** Everything that
  talks to Discord goes through it. **Never** call `discord.com` from anywhere else. This is exactly
  what makes the whole 401/403/tier matrix testable in-process with a fake (`tests/Api.Tests`).
- **Roles come from the bot token, by doc mandate.** `GET /guilds/{guild}/members/{user}` with the
  **bot token** — the only path, because the `identify guilds` user scopes don't carry roles
  (`architecture.html:570`). The Discord app/bot-token/guild/role-map are **shared external config**
  (the same values the host's Discord bot uses) — **NOT a process dependency on kgsm-bot** (keystone
  §4). Hold our own copy in config; never reach into the bot.
- **Auth is ON by default.** `Api__AuthDisabled=true` swaps in `DisabledAuthHandler` (synthetic
  admin — the pre-M4 open window), loudly logged. Never enable it on an exposed host.

## Invariants when you touch this

- **Secure-by-default.** A `FallbackPolicy` requires an authenticated caller, so a **new endpoint is
  gated unless it opts out**. Only `/health` + `/api/v1` carry `[AllowAnonymous]` (pre-login
  reachability). Adding an open endpoint is a deliberate, reviewed act — not an omission.
- **Tier gating** (hierarchical: admin ⊇ operator ⊇ viewer): viewer = reads + the `/stream` WS,
  operator = the command `POST`, admin = diagnostics + reserved (settings/install/audit-config).
  `401` = no/invalid bearer (challenge); `403` = authenticated, tier too low (forbid) — keep that split.
- **Honest failure modes** (the security analog of never-fabricate-a-status): Discord unreachable →
  `DiscordAuthException` → `502`, **never a default grant**; `none`/not-in-guild → terminal `403`; a
  failed role lookup is **never** silently downgraded to a softer tier.
- **A refresh token is never an access bearer.** `OnTokenValidated` rejects `tkn != "access"` on
  protected calls; only `/auth/session/refresh` reads a refresh token (from the `Authorization` header).
- **WS bearer rides `?access_token=`** (a handshake can't set a header) — read in JwtBearer's
  `OnMessageReceived` for the `/stream` path. Don't tear down a live socket on mid-stream expiry.
- **`/auth/session` returns the login-time profile snapshot** embedded in the token claims, NOT a live
  re-fetch — the Discord token is discarded at callback, so "fetched live" can't hold (a §6 divergence).

- **OAuth `state` CSRF (M4·b).** `/start` sets a one-time HttpOnly `state` cookie (`kgsm_oauth_state`);
  `/callback` requires the echoed `state` to equal the cookie (constant-time) *before* any Discord
  exchange, else `400 invalid_state`, then clears it (no replay). **Stateless** — a client cookie, no
  server store (honors the no-session-table decision). `SameSite=Lax` (NOT Strict — it must ride
  Discord's top-level redirect back), `Secure = Request.IsHttps` (off on http loopback, on under
  https), `Path=/auth/discord`. Don't switch it to a server-side pending-state store.

## M4 status — backend built & live-validated (2026-06-15)

`DiscordIdentityResolver` (the real HTTP impl) is now **live-validated** on the trusted host: a real
Discord login resolved an in-guild member's roles → `admin`, minted the bearer, and that bearer passed
live tier-gating end-to-end (PLAN.md §8 M4·b). The login endpoints `503` only until
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
