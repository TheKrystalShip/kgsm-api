# CLAUDE.md — Services/Auth/

Auth — **Discord per-host, Model A** (`architecture.html §3·f`, keystone O5). Identity is a
global Discord SSO anchor; **authorization is a short-lived host-scoped bearer** this host mints
after verifying identity once and resolving the role via the host's bot. Built at **M4·a**; the
authority for the contract is `PLAN.md §6` (auth row) + `§8` (M4·a log). This file is the local
"what you must not break."

## Locked decisions (do not relitigate)

- **Bearer = stateless JWT.** HMAC-SHA256; access ~15 min + refresh with a **30-day absolute cap**
  (was 8h; widened 2026-06-23 by user directive — a trusted, role-restricted friends group values
  staying signed in for weeks over a strict refresh window; the access TTL still bounds privilege).
  **No session table, no user row** — honors §3·f "no user row anywhere" and keeps M5 the first EF
  migration. Don't add a `sessions` entity or a server-side token store. Trade accepted: no instant
  revocation (bounded by the short access TTL). ⚠ A multi-week refresh token only actually survives
  if `KGSM_API_AUTH_SIGNING_KEY` is **stable** — an ephemeral per-process key invalidates every token
  on restart (the ctor logs a warning).
- **`IDiscordIdentityResolver` is the seam — the one chokepoint to `discord.com`.** Everything that
  talks to Discord goes through it. **Never** call `discord.com` from anywhere else. This is exactly
  what makes the whole 401/403/tier matrix testable in-process with a fake (`tests/Api.Tests`).
- **Roles come from the bot token, by doc mandate.** `GET /guilds/{guild}/members/{user}` with the
  **bot token** — the only path, because the `identify guilds` user scopes don't carry roles
  (`architecture.html:570`). The Discord app/bot-token/guild/role-map are **shared external config**
  (the same values the host's Discord bot uses) — **NOT a process dependency on kgsm-bot** (keystone
  §4). Hold our own copy in config; never reach into the bot.
- **Auth is ON by default.** `KGSM_API_AUTH_DISABLED=1` swaps in `DisabledAuthHandler` (synthetic
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
`KGSM_API_AUTH_DISCORD_*` are configured. **What's still owed for the *full* M4: only the frontend gate**
(the per-host session state machine + tier-gated controls — the SPA, still `planned`). Op note: dev ran an
**ephemeral** signing key (`KGSM_API_AUTH_SIGNING_KEY` blank → tokens die on restart) — set a stable secret
on any real host. To run with the dev creds, the env must be `Development` (`ASPNETCORE_ENVIRONMENT=Development`)
or `appsettings.Development.json` is ignored and the login endpoints `503` as if unconfigured.
