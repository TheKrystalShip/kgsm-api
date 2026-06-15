# CLAUDE.md — Services/Auth/

Auth — **Discord per-host, Model A** (`architecture.html §3·f`, keystone O5). Identity is a
global Discord SSO anchor; **authorization is a short-lived host-scoped bearer** this host mints
after verifying identity once and resolving the role via the host's bot. Built at **M4·a**; the
authority for the contract is `PLAN.md §6` (auth row) + `§8` (M4·a log). This file is the local
"what you must not break."

## Locked decisions (do not relitigate)

- **Bearer = stateless JWT.** HMAC-SHA256; access ~15 min + refresh with an **8h absolute cap**.
  **No session table, no user row** — honors §3·f "no user row anywhere" and keeps M5 the first EF
  migration. Don't add a `sessions` entity or a server-side token store. Trade accepted: no instant
  revocation (bounded by the short access TTL).
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

## M4·a vs M4·b — the honesty boundary

`DiscordIdentityResolver` (the real HTTP impl) is **built but un-live-validated**. The login
endpoints `503` until `KGSM_API_AUTH_DISCORD_*` are configured. **M4·b** (owed, on the trusted host
once a Discord app + bot token + guild + role-map exist) live-validates the three Discord calls and
adds the OAuth **`state` CSRF round-trip** (generated at `/start` today, not yet verified on callback).
Until then, don't claim the live flow works — the fake-tested logic is proven, the wire correctness is not.
