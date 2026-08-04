# Session Management & Revocation — Milestone Plan (M4·c)

**Status:** Decisions LOCKED 2026-07-08 · build not started · **Scope:** kgsm-api only
· **Re-opens:** `Services/Auth/CLAUDE.md:11-18` (the "no session table" half of the
stateless-JWT lock) · **Supersedes/restructures:** the earlier "Group E (11, 12, 13)"
planning surface (that was audit-as-session-history placebo + mint-time `expiresAt` +
wire-string tier display; #11 moves here off audit onto a real registry, #12 is unchanged,
#13 needs no backend work)

> **Cold-resume contract.** This doc is self-contained: a fresh session with no prior
> memory can read §0 (where we are) → §1–2 (why we re-open a locked decision) → §3 (the
> decision record) → §4 (the locked decisions table) → §5 (the design) and pick up exactly
> where the last session stopped. **Every increment ends in a Definition-of-Done; when you
> finish one, flip its box in §0 and write the commit/verify result into its "Done" line.**
> Trust §0's ledger over any assumption about progress. Before touching auth code, read
> `src/Api/Services/Auth/CLAUDE.md` (the lock doc — **this milestone updates it**, see §7).

---

## 0 · Progress ledger (update this as you go)

| # | Increment | Status | Done-marker (commit / verify result) |
|---|---|---|---|
| 1 | `SessionEntry` entity + DB context wiring + `EnsureCreated` | ☑ **BUILT** | build 0-warn green; tests **615/615** (no regression); smoke 78 passed (the 4 pre-existing M5/M2/M7 failures are unrelated — audit/console/assistant paths, not the sessions table) |
| 2 | `sid` JWT claim + `SessionTokenService` mint changes | ☑ **BUILT** | build 0-warn green; tests **615/615** (no regression). Call sites updated inline (login generates `sid_<guid>`; refresh carries sid from the refresh token's claims) so build stays green; the SessionEntry row write + audit Meta `sid` stamp wait for Increment 3, the ExpiresAt wire waits for Increment 7. ⚠ A pre-M4·c refresh token (no `sid` claim) now `401`s on `/refresh` (ReadRefreshAsync returns null) — D10 clean break at the refresh surface, intentional. |
| 3 | Login flow creates a session row + `auth.login` Meta gains `sid` | ☑ **BUILT** | build 0-warn green; tests **617/617** (+2 new `SessionRegistryTests`: sid-bearing token + session row lands; audit `meta.sid` links the two). The login path now persists the session row + stamps the forensics link; the validator that *enforces* it lands in Increment 4. The Logout path also stamps its audit `meta.sid` (read off the calling bearer) — the actual session-row `Revoked=true` write is Increment 5. |
| 4 | Per-request session validation (cached) + in-flight-token clean break | ☑ **BUILT** | build 0-warn green; tests **624/624** (+7 new `SessionValidatorTests`: valid→200 / revoked→401 / expired→401 / pre-M4·c-no-sid→401 / sessions-disabled-bypass→200 / cache-window-serves-stale-true-then-expires-to-401 / Evict→instant-401). The D10 clean break is LIVE: every authenticated request (REST + SSE) now checks the sid claim + the session registry; a pre-M4·c token (no sid) 401s; a revoked/expired session 401s within ≤5s (cache TTL) / ~instant (Evict on revoke, lands in Increment 5/6). The 56 existing tier-matrix tests now exercise the real production path (their test-token helper inserts a real session row). ⚠ Production note: any pre-deploy refresh token stops authorizing the moment the Increment 4 binary ships (no sid claim → 401 → relogin). Smoke unchanged (the 4 pre-existing failures unrelated — smoke runs under Api__AuthDisabled=true so the JwtBearer pipeline never fires). |
| **4·b** | **Rolling refresh-token rotation** (OFF-PLAN — user directive, added between Inc 4 and 5; **supersedes D8 "no sliding" + D9 "no rotation"**) | ☑ **BUILT** | build 0-warn green; tests **628/628** (+4 new `SessionRotationTests`: slides-Expires+bumps-LastSeen+rotates-jti / new-refresh-chains / **reuse-detection** old-refresh→401 / **logout revokes** access→401). What changed vs the original plan: (1) every `/auth/session/refresh` now rotates **both** tokens — the row's `Expires` **slides** to `now + REFRESH_ABSOLUTE_DAYS` (rolling window: a session used ≥once inside the window stays alive; an idle one still dies N days after last use) + `LastSeen` bumps; (2) a per-token **`jti`** claim + a `SessionEntry.CurrentJti` column give **reuse detection** — a stale/old/stolen refresh token (jti ≠ row's CurrentJti) → 401; (3) `RefreshResponse` gains a **`refresh`** field (the SPA MUST adopt both tokens each call — the old refresh is dead) → a **breaking wire change** vs D9's "additive only"; (4) `/auth/logout` now **revokes server-side** (`SessionStore.RevokeAsync` + cache `Evict`) — this is Increment 5's logout half, landed early because it was the natural fix for the build. A pre-rotation refresh token (no `jti`) 401s (same clean-break posture as the sid check). Null-`CurrentJti` first-refresh "adoption" branch handles the one-shot prod `ALTER TABLE`. **Remaining for Inc 5:** the *refresh-side* session-row validity is already enforced (via `UpdateForRefreshAsync`); only the plan's `RefreshResponse.expiresAt` (Inc 7) is still owed there. ⚠ Prod DB needs `ALTER TABLE sessions ADD COLUMN "CurrentJti" TEXT` (one-shot, alongside D11's table create). |
| 5 | Refresh + logout honor the session (revoke on logout; same-`sid` rotation on refresh) | ◪ **partial** (logout-revoke + refresh-session-check landed in 4·b) | logout server-side revoke + refresh reuse/validity check are DONE (see 4·b). `RefreshResponse.expiresAt` is now **DONE** (Increment 7, below). Left: a live OAuth soak. |
| 6 | Revocation endpoints (`/auth/sessions` + revoke + admin cross-user) + new `auth.session.*` audit actions | ☑ **BUILT** | build 0-warn green; tests **645/645** (+17 new `SessionRevocationTests`, no regression on the 628 baseline). New `Controllers/SessionController.cs`, root-routed (not `/api/v1`): `GET /auth/sessions` (viewer self / admin `?userId=` override, `current` flag on the caller's own sid), `POST /auth/session/revoke` (self, body `{sid?,all?}` — logout-equivalent when neither is set, `404` on a sid the caller doesn't own, `400` if both are set), `POST /auth/sessions/{sid}/revoke` (admin, cross-user, `404` on an unknown sid), `POST /auth/users/{userId}/sessions/revoke-all` (admin, "log out everywhere", always `204` — a target with no active sessions is not an error). `SessionStore` gained `ListActiveAsync`/`GetByIdAsync`/`RevokeAllForUserAsync`; every revoke evicts the validator cache per affected sid (D2's ~instant path, same posture as `/auth/logout`). Three new direct-write audit actions (D12): `auth.session.revoke`/`.revoke.all` (self, info), `.revoke.admin` (admin-on-others, warn — covers BOTH admin endpoints, not a 4th action). Tier gating: class-level viewer + method-level admin on the two admin endpoints (AND-combines — a viewer hitting either gets `403`, confirmed live in tests). Two ambiguities the plan didn't spell out, resolved plan-consistently (recorded in the controller's doc-comments): a non-admin's `?userId=` is silently ignored (scoped to self, not `403`); `{sid,all}` both set → `400` (not a silent priority pick). |
| 7 | `/me.recentLogins` from audit + mint-time `expiresAt` on `CallbackResult`/`RefreshResponse` (the carried-over Group E #11/#12) | ☑ **BUILT** | build 0-warn green; tests **650/650** (+5 new: 2 `CallbackResult` expiresAt-present/omitted, 1 `RefreshResponse.expiresAt`-≈15min, 2 `/me.recentLogins` after-real-login/fresh-actor-empty; no regression on the 645 baseline). **Piece A** — new `AuditQueries.RecentByActionAsync` (exact-`Action` equality, NOT `PageAsync`'s category-prefix, which would also pull `auth.logout`); `MeController` gained its first DB read (`AppDbContext` injected primary-ctor-style, like `AuditController`), querying the last 10 `auth.login` rows by the bearer's bare `id.Username` (NOT the `discord:`-prefixed handle) and mapping `meta.userAgent` → `RecentLogin.Device` via the existing `AuditMapping.ToRecord` parse (no duplicated JSON-parse logic). **Closed the plan's flagged gap:** `auth.login`'s audit meta only carried `tier`+`sid`, not `userAgent` — `AuthController.RecordAuthAsync` gained an additive `userAgent` parameter, threaded from the already-computed login-time UA (the same value written to the `SessionEntry` row); the logout call site passes `null` (recentLogins only reads logins). Additive to an *existing* direct-write, no new writer/action (invariant #5 intact). **Piece B** — `CallbackResult` gained tail `AccessTokenExpiresAt`/`RefreshExpiresAt` (`WhenWritingNull`-omitted, so the denied branch — which now passes `null, null` — stays wire-identical); `RefreshResponse` gained a non-nullable tail `ExpiresAt`. Both call sites already had `access.ExpiresAt`/`refresh.ExpiresAt` available (`MintedToken`, since Increment 2). OAuth fragment-redirect handoff left untouched per spec (JSON-contract-only this increment). **#13** confirmed no backend work — `MeResponse.Tier` already on the wire. One doc-comment ambiguity resolved: the plan didn't specify how `RecentByActionAsync` should shape its return — kept it returning raw `AuditEntry` rows (mirroring `PageAsync`'s internal shape) and did the `RecentLogin` projection in the controller via the existing `AuditMapping.ToRecord`, rather than adding a second Meta-JSON-parsing path. |
| 8 | Session GC worker (delete expired rows) | ☑ **BUILT** | build 0-warn green; tests **655/655** (+5 new `SessionCleanupTests`, no regression on the 650 baseline). New `SessionStore.DeleteExpiredAsync(now)` — one indexed `ExecuteDeleteAsync` bulk delete on `ix_sessions_expires`, gated on the same `_writeGate` as every other store write; deletes rows regardless of `Revoked` (expired is dead either way — pinned by two tests, revoked+expired and not-revoked+expired). New `SessionCleanupWorker : BackgroundService` (mirrors `MetricsMaintenanceService`): startup catch-up pass + a `PeriodicTimer` on `Api__SessionsGcMs` (default 10min, floor 60s already applied in `ApiOptions`); logged at debug per pass, per-tick exceptions swallowed (non-cancellation). **Inert when `SessionsEnabled=false`** (`Api__SessionsDisabled=true`) — `ExecuteAsync` logs once and returns with no timer at all (not a looping no-op). Registered `services.AddHostedService<SessionCleanupWorker>()` in `Startup.cs` beside the other session services. No schema change (deletes from the existing table, no `Migrations/`). `Api.csproj` bumped 0.16.0 → 0.17.0; `CHANGELOG.md ## [Unreleased]` updated. |
| 9 | Lock-doc update (`Services/Auth/CLAUDE.md`) + `PLAN.md §6` contract freeze + `CHANGELOG` + version bump | ◪ **partial** | **Done:** the authority docs are reconciled to current canon — `Services/Auth/CLAUDE.md` locked-decisions block rewritten (session registry / cached per-request `sid` check / sliding-window refresh + `jti` rotation-reuse-detection / ≤5s revocation) + a session-registry status section; root `CLAUDE.md` M4 status line + invariant #5 (session table = operational state); `PLAN.md §4` M4·c milestone entry. The four `KGSM_API_SESSIONS_*` keys were already documented in `appsettings.json` during the build increments. **Still owed:** the `PLAN.md §6` frozen contract row + the `§8` validation-log entry (add after the live soak); the `CHANGELOG` already carries the per-increment entries (0.16.0/0.17.0) — no separate milestone rollup written; `Api.csproj` left at 0.17.0 (per-increment bumps, not the plan's original single 0.2.0). Live OAuth soak still owed (§5/§8). |

**Ordering:** 1 → 2 → 3 → 4 are strictly ordered (each needs its predecessor). 5 sits
naturally after 4. 6 needs 5. 7 is independent and parallel-safe (no auth-pipeline touch).
8 can land any time after 1. 9 is the **close-out** step — do it last so the authority docs
don't describe a half-built milestone.

---

## 1 · The problem

The auth boundary (`M4·a/M4·b`) is **stateless JWT, no session table, no user row** —
access ~15 min, refresh with a **30-day absolute cap** (widened 2026-06-23 by user
directive; was 8h). The locked decision (`Services/Auth/CLAUDE.md:11-18`) lists the trade
as: *"no instant revocation (bounded by the short access TTL)."*

**That trade is honest for the access token — and only for the access token.** A refresh
token silently mints fresh 15-min access tokens for 30 days with no kill switch except
rotating `Api__SigningKey`, which nukes **every** user (collateral blast). At 8h
the gap was tolerable; at 30d (the user directive postdated the lock) it's meaningfully
riskier. A compromised browser / shared device / leaked dev-tools token isn't covered by
"trusted friends group."

**A second symptom surfaced while planning the Settings page.** The original "Sessions"
idea had nowhere honest to read active sessions from: the audit log is append-only
provenance (it can't revoke), so leaning on it for session *control* would have been a
placebo.

Both symptoms have the same root cause: **there is no operational session state.** This
milestone adds it, narrowly, and upgrades the revocation surface from "rotate the signing
key and betray every user" to "revoke one session, or all of a user's sessions, in ≤5s."

---

## 2 · What this unlocks · what it does NOT do

**Unlocks:**
- "Log me out everywhere" — revoke all of a user's sessions (≤5s effective; access TTL
  hard ceiling ≤15min).
- "Log out this device" — revoke one session by `sid`.
- **Admin revokes another user's sessions** — extends the tier matrix (admin becomes a
  session operator, not just a config operator — substantial power; deliberate user choice).
- A real **Active Sessions** display on the Settings page — reads from the registry, not
  the audit placebo.
- `POST /auth/logout` actually ends a session server-side, not just client-side.

**Deliberately does NOT (scope 1 boundaries — recorded so a later increment can pick
them up):**
- **Not** per-request DB hits without cache — the 5s in-memory TTL cache bounds it; the hot
  path stays light (the 1s metrics SSE reconnect ratio doesn't translate to a DB read per
  reconnect).
- **Not** refresh-token rotation / `jti` reuse-detection — scope 1 keeps a stable refresh
  token for the session's lifetime (the SPA keeps the one token it got at login). This
  avoids a wire break on `RefreshResponse`. Rotation + reuse-detection is a later increment
  (see §6, Open items).
- **Not** re-checking Discord roles at refresh — orthogonal gap; a role change still takes
  effect at the next full OAuth bounce (≤30d). The session registry doesn't fix this; a
  `forceReauth` flag could (later).
- **Not** the "user row" half of the lock — no `UserProfile` entity, no display-name
  store. Identity is still the JWT-claim snapshot at login time. Only operational session
  state is persisted.
- **Not** cross-host session control — sessions are host-scoped (`aud = hostId`), matches
  the per-host topology.
- **Not** instant (sub-second) access-token kill — 5s cache lag is the accepted trade. A
  hard delete propagates within 5s.

---

## 3 · Decision record — re-opening a locked decision

`Services/Auth/CLAUDE.md:9-18` reads, *"Locked decisions (do not relitigate): Bearer =
stateless JWT … No session table, no user row — honors §3·f 'no user row anywhere' and keeps
M5 the first EF migration. Don't add a `sessions` entity or a server-side token store."*

Re-opening the **session-table** half is serious, required, and the rationale for reopening
(to be recorded in the lock doc at close-out):

- The lock's stated rationale ("honors §3·f 'no user row anywhere'" + "keeps M5 the first
  EF migration") **conflates user profile rows** (what §3·f actually bars — data that
  follows a user across devices) with **operational session state** (revocation security). A
  refresh/session registry is the latter, not the former.
- The "trade accepted: no instant revocation (bounded by the short access TTL)" rationale is
  honest for the access token but **does not honestly bound the 30-day refresh**, which is
  exactly the token that silently mints fresh access tokens. The 30-day widening
  (user directive 2026-06-23) postdated the lock and materially weakens it.
- The audit log is being leaned on as a *session-history placebo*. Audit is single-writer
  append-only provenance; it cannot revoke. This milestone separates the two concerns
  honestly: **audit for past events, registry for current sessions.**

**The user-row half of the lock stays locked.** Identity still comes from JWT claims; no
`UserProfile` entity. The change is scoped to operational state only.

---

## 4 · Locked decisions

| # | Decision | Value | Status |
|---|---|---|---|
| D1 | **Session registry** | A `SessionEntry` table per-row per (login × device). The authority on "is this session still alive." | **LOCKED** (user, 2026-07-08): stateless JWT cannot revoke a 30d refresh any other way |
| D2 | **Per-request check** | YES — but **cached**: `IMemoryCache` 5s TTL keyed by `sid`, evicted on revoke. Hot path stays light. | **LOCKED** (user, 2026-07-08): chose "full session table, per-request check" after being shown the cost matrix; cache = the accepted ~5s revocation lag |
| D3 | **Active-sessions display source** | The new registry (per the user's pick). Audit keeps doing historical/provenance recent-logins (Group E #11 carries over — see Increment 7). | **LOCKED** (user, 2026-07-08): the registry IS the honest authority on active sessions |
| D4 | **Admin cross-user revoke** | YES — admin can revoke any user's sessions. Viewer is self-only. | **LOCKED** (user, 2026-07-08): deliberate — admin becomes a session operator, not just a config operator |
| D5 | **Session row fields** | `Id, UserId, HostId, Created, LastSeen, Expires, UserAgent (raw, no IP), Revoked, RevokedAt` | **LOCKED** (user, 2026-07-08): UA-only (no IP — identifying churn + PII surface), raw UA string for display ("Chrome • Windows 10") |
| D6 | **Soft-delete + GC** | Revoke = `Revoked=true`/stamp; expired rows deleted by a 10-min maintenance worker. Active set query is `WHERE !Revoked AND Expires > now`. | **LOCKED (impl call):** keeps the audit-shaped "actions don't vanish" provenance posture; GC deletes only dead rows |
| D7 | **`sid` claim** | A JWT `sid` claim (opaque `sid_<guid>`) stable across a session's lifetime (carried by access + refresh; survives access rotation). **No `jti` in scope 1.** | **LOCKED (impl call):** minimal — `jti` only needed for refresh rotation/reuse-detection, which is a later increment |
| D8 | **Session TTL** | ~~`Expires = Created + 30d` absolute, no sliding~~ → **SUPERSEDED (user directive, Inc 4·b): SLIDING.** `Expires` starts at `now + 30d` at login and **slides to `now + 30d` on each successful refresh** — a session used ≥once inside the window stays alive; an idle session dies 30d after its last refresh. `LastSeen` bumped on refresh. | ~~LOCKED~~ **SUPERSEDED (2026-07-09):** user wanted "open the site once before expiry → stays logged in." Built in Inc 4·b. |
| D9 | **Refresh rotation** | ~~NOT in scope 1; stable refresh token; `RefreshResponse` non-breaking~~ → **SUPERSEDED (user directive, Inc 4·b): ROTATION IS IN.** Every refresh rotates both tokens; a per-token `jti` + `SessionEntry.CurrentJti` give **reuse detection** (stale jti → 401). `RefreshResponse` gains a **`refresh`** field — a **breaking wire change** the SPA adopts (both tokens each refresh). | ~~LOCKED deferral~~ **SUPERSEDED (2026-07-09):** the sliding window (D8) only stays safe with rotation + reuse-detection. Built in Inc 4·b. |
| D10 | **In-flight tokens** | Clean break. Pre-`sid` refresh tokens fail the per-request check (`401`) → SPA bounces to login. **No grandfathering.** On a live host: announce a relogin. Trusted-friends host — acceptable. | **LOCKED:** grandfathering would defeat the security point of the milestone |
| D11 | **Migration** | `EnsureCreated` creates the `sessions` table automatically on a fresh DB (registered in `AppDbContext.OnModelCreating` like every other table — `EnsureCreated` includes it; **no** `EnsureSchemaAsync` raw-DDL layer in `SessionStore`). For the **existing prod DB** at `/var/lib/kgsm-api/kgsm-api.db`, a **one-shot `sqlite3` table-creation command** is run once in place before the first deploy of the new code — no C# migration code, no wipe, audit rows untouched. The command is **not kept** in the repo; it runs once and that's it. Afterward the code assumes the table exists. No migration management in C# — `EnsureCreated` handles fresh deploys, the one-shot command handled the existing prod DB. **⚠ In-flight tokens still break at deploy (D10)** — every existing refresh token lacks the `sid` claim → per-request check fails → 401 → SPA bounces to login. | **LOCKED (impl call):** `EnsureCreated` includes the new table on a fresh DB; one-shot `sqlite3` DDL for the existing prod DB; no `Migrations/` introduced without re-deciding the migration lock |
| D12 | **New audit actions** | `auth.session.revoke` (self, info), `.revoke.all` (self "log out everywhere", info), `.revoke.admin` (admin-on-others, warn). All direct-write (no kgsm event → no double-write risk — same posture as `auth.login`/`auth.logout`). Meta carries `sid` + `userId` of the target. | **LOCKED (impl call):** adds to the closed vocabulary at producer-time only (the producers land in this milestone) |
| D13 | **Milestone number** | **M4·c** (continues M4 — re-opens an M4 lock; not M9+ as standalone). | Plan-level |

---

## 5 · The design

> Conventions inherited from the repo: JSON camelCase + ISO-8601 UTC `Z`; the `{error}`
> envelope; `EnsureCreated` not migrations (dev authority — wipe the DB on schema change);
> config via `appsettings.json` keys each overridable by a same-named `KGSM_API_*` env var;
> hosted services registered in `src/Api/Startup.cs`; the JWT pipeline (validation params +
> `OnTokenValidated`) lives in `Startup.cs:349-362`; smoke (`scripts/smoke.sh`) + xUnit
> (`tests/Api.Tests/`) are the two proof surfaces. Invariants: #1 honest (UA from header;
> expiry from just-minted; revocation from real state) ✓ #4 additive-only in `/api/v1` ✓ (the
> `/auth` refresh wire change adds a field, doesn't break the existing one) #5 audit still
> single-writer; new audit actions are additive to the vocabulary, still direct-write ✓.

### Config keys (introduced; document all in `appsettings.json`)

| Key | Default | Meaning | Increment |
|---|---|---|---|
| `Api__SessionsDisabled` | `false` | master switch; `true` → session registry inert, no per-request check, no revoke surface (escape hatch for debugging) | 1, 4, 6 |
| `Api__SessionsCacheTtlMs` | `5000` (floor `500`) | in-memory cache TTL (the revocation-lag bound) | 4 |
| `Api__SessionsGcMs` | `600000` (10 min) | how often the GC worker deletes expired rows | 8 |
| `Api__SessionsRefreshAbsoluteDays` | `30` | mirrors `SessionTokenService.RefreshTtl`; the `Expires` column is `Created + this`. (Lock doc note: stays in lockstep with the JWT refresh TTL — if you change one, change both.) | 1 |

### Schema (D5) — created via `AppDbContext` + `EnsureCreated` (same file as the audit table per D11)

```sql
CREATE TABLE sessions (
  "Id"         TEXT NOT NULL CONSTRAINT "PK_sessions" PRIMARY KEY,
  "UserId"     TEXT NOT NULL,
  "HostId"     TEXT NOT NULL,
  "Created"    INTEGER NOT NULL,      -- UTC ticks (ValueConverter, like AuditEntry.Ts)
  "LastSeen"   INTEGER NOT NULL,      -- UTC ticks
  "Expires"    INTEGER NOT NULL,      -- UTC ticks — the validator's Expires > now is a translatable INTEGER >=
  "UserAgent"  TEXT NULL,
  "Revoked"    INTEGER NOT NULL DEFAULT 0,
  "RevokedAt"  INTEGER NULL           -- UTC ticks, nullable
);
CREATE INDEX "ix_sessions_user"    ON sessions ("UserId", "Revoked", "Expires");
CREATE INDEX "ix_sessions_expires" ON sessions ("Expires");
```

`_db.Sessions` is the `DbSet<SessionEntry>` on the **existing** `AppDbContext` (one more
table beside `AuditEntry` etc.). No second `DbContext` — sessions are low-churn (a few rows
per user; only `LastSeen` rotates, ~15min/user). A dedicated DB file would only reintroduce
the cross-context coordination cost. The `SessionEntry` entity uses `ValueConverter`s on
`Created`/`LastSeen`/`Expires`/`RevokedAt` (the same posture as `HostSettingsEntity.UpdatedAt`
at `AppDbContext.cs:80-83` and `AuditEntry.Ts` at `:140-142`), so the `Expires > now` query
in `SessionValidator` is a translatable `INTEGER >=` comparison rather than a `TEXT`
comparison EF can't translate (SQLite has no date type; EF stores `DateTimeOffset` as `TEXT`
but emits no comparison SQL — which the validator's expiry filter needs). The one-shot prod
`sqlite3` command mirrors this DDL byte-for-byte.

### Increment 1 — `SessionEntry` entity + DB context wiring  ·  ☐
- **Goal:** the table exists, `EnsureCreated` makes it on a fresh DB, `AppDbContext` exposes
  it. On the existing prod DB the table is added by the one-shot `sqlite3` command (D11) —
  no in-code DDL.
- **Build:**
  - New `src/Api/Data/SessionEntry.cs` — EF entity matching the corrected schema (`ValueConverter`s
    on the four `DateTimeOffset` fields — same as `HostSettingsEntity.UpdatedAt`; `Revoked`
    as `bool`).
  - Add `DbSet<SessionEntry> Sessions { get; }` to `AppDbContext` (`src/Api/Data/AppDbContext.cs`).
  - Add the `OnModelCreating` config: table name `sessions`, PK `Id`, the two indexes, the
    four `ValueConverter`s. `EnsureCreated` then creates the table automatically on a fresh DB.
  - Master switch `Api__SessionsDisabled` plumbed into a small options class
    (`SessionOptions`, alongside `ApiOptions`) — **read but inert** in this increment (the
    check that consumes it lands in Increment 4).
- **Verify:** build green, 0-warning; existing tests still pass (no behaviour change yet —
  the table just exists, nothing writes it); on a fresh (wiped) DB, `EnsureCreated` creates
  the `sessions` table; the one-shot prod command mirrors the same DDL.
- **Done when:** the table is created fresh on a new DB; `AppDbContext.Sessions` is
  queryable; the master switch is read from config; no existing test breaks; no in-code DDL
  beyond the `OnModelCreating` mapping.

### Increment 2 — `sid` JWT claim + `SessionTokenService` mint changes  ·  ☐
- **Goal:** the minter emits a `sid`; the public API returns expiry alongside the token.
- **Build:**
  - New constant `AuthClaims.SessionId = "sid"` in `Services/Auth/AuthTier.cs:64-80`.
  - New immutable record in `Services/Auth/SessionTokenService.cs`:
    `public sealed record MintedToken(string Token, DateTimeOffset ExpiresAt);`
  - Change `ISessionTokenService` signatures (`:15,19`) and impl (`:69-73`) from `string`
    to `MintedToken`. `MintAccess`/`MintRefresh` gain a `string sessionId` parameter.
  - `private MintedToken Mint(...)` already computes `Expires = DateTime.UtcNow.Add(ttl)`
    (`:95`) — return it as `new MintedToken(token, new DateTimeOffset(exp, TimeSpan.Zero))`.
  - Add the `sid` claim to the `claims` list alongside `sub`/`tier`/`host`/`tkn`/etc.
- **Verify:** unit-test the minter: `sid` round-trips through validate; `ExpiresAt` matches
    the configured TTL (±1s); a missing `sid` claim is detected.
- **Done when:** tokens carry `sid` + the public surface returns expiry. **No callers
    updated yet** — the two call sites (`AuthController.cs:115-116`, `:176-177`) break the
    build until Increment 3 + Increment 5 land them. (Either land Increment 3 in the same
    commit, or keep the call sites updated inline with a temporary sid=mint path —Increment
    3 covers it.)

### Increment 3 — Login flow creates a session row + `auth.login` Meta gains `sid`  ·  ☐
- **Goal:** every successful OAuth login writes a `SessionEntry` row and stamps the audit
  event with the link.
- **Build:** `src/Api/Controllers/AuthController.cs` — the `Callback` action (`:67-129`):
  - Generate `sessionId = "sid_" + Guid.NewGuid().ToString("N")` right after tier resolution
    (`:109-113`).
  - Pass `sessionId` into `tokens.MintAccess`/`MintRefresh` (Increment 2's new signatures).
  - After token mint (`:115-116`), before the existing `auth.login` audit write (`:120`):
    `INSERT` a `SessionEntry` (Id=sessionId, UserId=userHandle, HostId=options.HostId,
    Created=now, LastSeen=now, Expires=now+30d, UserAgent=Request.Headers.UserAgent,
    Revoked=false). Use a new `Services/Auth/SessionStore.cs` (a small service over
    `AppDbContext`, similar shape to `Services/Aggregation/HostSettingsStore.cs`).
  - The shared `RecordAuthAsync` helper (`:235-257`) gains a `sid` parameter (or reads it
    from the just-minted tokens); the Meta dict (`:250`) gains `["sid"] = sessionId`.
  - Return `CallbackResult(...access.Token, refresh.Token, ... access.ExpiresAt,
    refresh.ExpiresAt)` — the DTO change itself is Increment 7's, listed here so the call
    site compiles. (Either ship Increment 7 first, or carry both in one commit — they share
    the construction site.)
- **Verify:** xUnit — fake Discord resolver drives a callback; assert the sessions table has
  a row with the right `UserId`/`HostId`/`Expires`/`UserAgent`; assert the `auth.login`
  audit row's `Meta["sid"]` matches; assert the minted tokens carry the `sid` claim.
- **Done when:** a real OAuth bounce (or the faked one) leaves exactly one session row +
  one linked audit row, with the right shape.

### Increment 4 — Per-request session validation (cached) + in-flight-token clean break  ·  ☐
- **Goal:** every authenticated request verifies the session is alive; revoked/expired =
  401. D10's clean break for pre-`sid` tokens lands here.
- **Build:**
  - New `Services/Auth/ISessionValidator.cs` + `SessionValidator.cs`:
    - `Task<bool> IsValidAsync(string sid, CancellationToken ct)`.
    - `IMemoryCache` (already registered by `AddMemoryCache()` if not present, add it in
      `Startup.ConfigureServices`). Keyed by `sid` → `bool`. On miss: query `Sessions` for
      `Id == sid && Revoked == false && Expires > now`; cache the result (including `false`
      so a revoked session doesn't keep DB-hammering). TTL = `Api__SessionsCacheTtlMs`.
    - **Evict** on revoke — `SessionStore.RevokeAsync` calls `cache.Remove(sid)` for each
      affected sid (best-effort; the 5s TTL is the backstop).
  - Extend `Startup.cs` `OnTokenValidated` (`:349-362`) — after the existing `tkn == access`
    check (`:354`):
    ```
    var sid = ctx.Principal?.FindFirst(AuthClaims.SessionId)?.Value;
    if (sid is null) { ctx.Fail("no session id (pre-M4·c token)"); return; }
    if (Api__SessionsDisabled) return;               // escape hatch — honor it
    var validator = ctx.HttpContext.RequestServices.GetRequiredService<ISessionValidator>();
    if (!await validator.IsValidAsync(sid, ctx.HttpContext.RequestAborted))
        ctx.Fail("session revoked or expired");
    ```
    Use `RequestAborted` so a client disconnect doesn't trip the cache falsely.
  - Honors the WS/SSE path (`OnMessageReceived` sets the token → `OnTokenValidated` fires
    → the check runs). No separate middleware to wire.
- **Verify:** xUnit — valid session → 200; revoked session → 401 within the cache window
  (test with `cache_ttl_ms=0` for determinism); expired session → 401 even if `Revoked=false`;
  pre-`sid` token → 401 "no session id"; disabled switch → all three pass. Cache probe:
  two requests within 5s produce one DB hit (assert via a counting probe).
- **Done when:** the hot path checks sessions cached; the clean break drops in-flight tokens
  honestly; the escape hatch is loud. **⚠ Side effect:** every existing live refresh token
  stops working at deploy time (D10). Smoke a forced-relogin before any live redeploy.

### Increment 5 — Refresh + logout honor the session  ·  ☐
- **Goal:** `/auth/session/refresh` validates the session before re-minting; `/auth/logout`
  revokes it.
- **Build:** `AuthController.cs`:
  - Refresh action (`:163-178`):
    - Read `sid` from the refresh token's claims.
    - `SessionStore.GetByIdAsync(sid)` → must exist, `!Revoked`, `Expires > now`. Else 401.
    - Mint a new **access** token with the **same `sid`** (no refresh rotation in scope 1, D9).
    - `UPDATE Sessions SET LastSeen = now WHERE Id = sid`.
    - Return `RefreshResponse(access.Token, tier, access.ExpiresAt)` (Increment 7's DTO).
  - Logout action (`:207-216`):
    - Read `sid` from the access bearer.
    - `SessionStore.RevokeAsync(sid)` → `UPDATE Sessions SET Revoked=true, RevokedAt=now
      WHERE Id=sid` (idempotent — already-revoked is fine). Evict cache.
    - (existing) `auth.logout` audit write — keep; Meta reuses the new `sid` plumbing from
      Increment 3.
    - Return 204.
- **Verify:** xUnit — refresh with valid sid → new access (same sid) + `LastSeen` advanced;
  revoked sid → 401; expired session → 401; logout revokes and the next bearer 401s.
- **Done when:** logout is real (revokes server-side); refresh is gated on a live session.

### Increment 6 — Revocation endpoints + new `auth.session.*` audit actions  ·  ☐
- **Goal:** the Settings Sessions page gets its control surface + admin cross-user power.
- **Build:** new `Controllers/SessionController.cs`:

| Endpoint | Method | Tier | Behavior |
|---|---|---|---|
| `/auth/sessions` | GET | viewer (self) / admin (`?userId=` for others) | Active set: `WHERE UserId==caller.sub && HostId==host && !Revoked && Expires>now` (viewer); admin override via `?userId=`. Fields: `sid, userId, created(Z), lastSeen(Z), expires(Z), userAgent, current?`. `current` = the sid on the calling bearer. |
| `/auth/session/revoke` | POST | viewer self | Body `{ sid? \| all: true }` (omit both = revoke the calling session = logout-equivalent). Returns 204. |
| `/auth/sessions/{sid}/revoke` | POST | admin | Revoke any session by sid (cross-user). Returns 204. |
| `/auth/users/{userId}/sessions/revoke-all` | POST | admin | "Log out user everywhere." Returns 204. |

  - Revoke ops: `SessionStore.RevokeAsync(...)` → `UPDATE Sessions SET Revoked=true,
    RevokedAt=now WHERE ...`; evict cache for each affected sid; best-effort audit row
    (`AuditAction.AuthSessionRevoke` / `.RevokeAll` / `.RevokeAdmin` per D12) via the
    existing direct-write path (`AuditService.AppendAsync`, like `auth.logout`).
  - DTOs (increment counts the contract-freeze — see §7):
    - `SessionRecord(string Sid, string UserId, DateTimeOffset Created, DateTimeOffset
      LastSeen, DateTimeOffset Expires, string? UserAgent, bool Current)` (Current only set
      on `/auth/sessions` for the caller's sid).
    - `RevokeRequest(string? Sid, bool? All)` — both nullable; exactly one set, or neither.
- **Verify:** xUnit tier matrix:
  - viewer self-revoke (`{ sid }`) → that sid's next access → 401; other sessions of the
    same user unaffected; the audit row lands as `auth.session.revoke` info with `sid` Meta.
  - self-revoke-all (`{ all: true }`) → all the caller's sessions die, incl. the calling
    one; audit `auth.session.revoke.all` info.
  - admin revoke another user's session → 200 for admin; the target's next access → 401;
    audit `auth.session.revoke.admin` warn. Viewer attempting it → 403.
  - `GET /auth/sessions` returns only the active set, `current` flag set correctly; admin
    `?userId=` returns another user's.
- **Done when:** the four endpoints work; tier matrix is green; audit rows land.

### Increment 7 — `/me.recentLogins` from audit + mint-time `expiresAt` (the carried-over Group E #11/#12)  ·  ☐
- **Goal:** surface kept honest — recent login history reads from audit (provenance),
  active sessions read from the registry (Increment 6); the SPA learns refresh-expiry at
  mint time.
- **Build:**
  - **#11 recent logins — from audit (read-only display, complements Increment 6's
    active-sessions surface):**
    - New `AuditQueries.RecentByActionAsync(db, action, actorName, limit, ct)` (a sibling of
      `PageAsync` at `:34` — `PageAsync` can't filter by exact `Action`, only by `category`
      prefix which would pull logouts too). Static + request-scoped `AppDbContext`, matches
      the existing read convention.
    - `Contracts/MeDto.cs` — extend `MeResponse` (currently `MeResponse(SessionUser, string
      Tier, IReadOnlyList<string> Scopes)` at `:18`) with `IReadOnlyList<RecentLogin>
      RecentLogins` **at the tail** (positional-clean). New
      `record RecentLogin(DateTimeOffset Ts, string? Device);`.
    - `MeController` (`:30-44`) — inject `AppDbContext db` (in-pattern; `AuditController.cs:24`
      does it), action becomes async, query the last 10 `auth.login` rows for `id.Username`,
      map to `RecentLogin(r.Ts, r.Meta?.GetValueOrDefault("userAgent"))`.
    - **Divergence to record:** this is `/me`'s first DB read (it has been pure-claims until
      now). Honest — `auth.login` is the single auth-event writer (no lastLogin column
      anywhere; no user row; invariant #5 untouched).
  - **#12 mint-time `expiresAt`:**
    - `Contracts/AuthDto.cs` — `CallbackResult` (`:16-21`) gains
      `DateTimeOffset? AccessTokenExpiresAt` + `DateTimeOffset? RefreshExpiresAt` at the
      tail (the denied-branch construction at `AuthController.cs:113` stays positional +
      `WhenWritingNull`-omitted — wire unchanged for that path). `RefreshResponse` (`:27`)
      gains `DateTimeOffset ExpiresAt` at the tail; only call site `:177` updates.
    - The `Callback` (`:129`) and refresh (`:177`) construction sites already updated in
      Increments 3 + 5 — this increment ships the DTO changeset itself so the wire is
      frozen.
  - **#13 tier display — no backend work** (wire string `MeResponse.Tier` already returned);
    SPA title-cases. Recorded here for cross-reference.
- **Verify:** xUnit — `CallbackResult` includes both `expiresAt`s on success, omits them
  on denied; `RefreshResponse` includes `expiresAt` ≈ now + 15min; `/me.recentLogins` returns
  ≥1 entry with the matching UA after a fake login, `[]` for a fresh actor.
- **Done when:** the wire surfaces are honest + frozen; `/me` has its first (read-only) DB
  query; #13 is closed (no backend work).

### Increment 8 — Session GC worker  ·  ☐
- **Goal:** permanent storage bound — expired rows deleted; the table doesn't grow forever.
- **Build:** new `Services/Auth/SessionCleanupWorker.cs` (`IHostedService` + `BackgroundService`,
  a `PeriodicTimer` with `Api__SessionsGcMs`):
  - `DELETE FROM sessions WHERE expires < <now>` — both revoked and not (expired is dead
    regardless of `Revoked`). Cheap indexed delete on `ix_sessions_expires`.
  - Run once at startup (catch-up after downtime — like the metrics maintenance worker,
    `Services/Metrics/MetricsMaintenanceService.cs`).
  - Logged at debug; inert when `Api__SessionsDisabled`.
- **Verify:** xUnit — seed an expired row, run the worker with a shortened interval (50ms),
  assert the row is gone and an in-window row survives.
- **Done when:** the table is bounded across a long run; the worker is inert under the
  master switch.

### Increment 9 — Lock-doc update + `PLAN.md §6` contract freeze + `CHANGELOG` + version bump  ·  ☐
- **Goal:** the authority docs describe what was built; the contract is frozen; the version
  reflects the user-facing change.
- **Build:**
  - **`Services/Auth/CLAUDE.md`** — the close-out edit (the milestone re-opens this lock;
    the doc must reflect it or it self-contradicts):
    - Strike the "no session table" clause in the locked-decisions block (`:11-18`).
      Replace with the M4·c rationale: "session registry for revocation; hot path stays
      stateless-with-cache; the user-row half stays locked." Reference this doc.
    - Mark the refresh-rotation/reuse-detection deferral (D9) — still-deferred.
    - Update the "no instant revocation (bounded by the short access TTL)" trade note with
      the new ~5s revocation (cache-bound) for sessions + the unchanged ≤15min access-TTL
      ceiling for the live access token.
  - **`PLAN.md`:**
    - Add `### M4·c` entry in §4 (the milestones section), marked `planned` (this doc is
      the plan; flip to `partial`/`built` at close-out).
    - Add a §6 contract registry row freezing the new wire shapes:
      - `SessionRecord`()) and the `current` flag.
      - The revoke endpoints: `POST /auth/session/revoke { sid? \| all: true }`,
        `POST /auth/sessions/{sid}/revoke`, `POST /auth/users/{userId}/sessions/revoke-all`.
      - The `/me.recentLogins[]` additive.
      - The `CallbackResult.accessTokenExpiresAt` / `.refreshExpiresAt` additive + the
        `RefreshResponse.expiresAt` additive.
      - The new `auth.session.*` audit actions (additive to the vocabulary).
      - State the one non-additive change explicitly: **the per-request session check
        rejects pre-`sid` refresh tokens (D10 — clean break).** The `/auth` refresh wire
        itself only adds a field; no existing client breaks except by this check, which is
        the security point.
    - Add a §8 validation-log entry at close-out (after live validation).
  - **Root `CLAUDE.md`** — extend the M4 line in the status paragraph with M4·c (no
    invariant changes; #5 audit still single-writer, the new session table is operational
    state not domain).
  - **`CHANGELOG.md ## [Unreleased]`** — a single milestone entry covering: session
    registry + revocation, `/auth/sessions`, self + admin revoke, `/me.recentLogins`,
    mint-time `expiresAt`, the pre-`sid`-token clean break.
  - **`Api.csproj`** — bump `<Version>` 0.1.0 → 0.2.0 (minor — new user-facing fields);
    tag `v0.2.0` at release.
- **Done when:** docs match the build; contracts frozen; version bumped.

---

## 6 · Open items / risks (recorded for the next session)

- **Hot-path cache pressure:** first request per `sid` per 5s window hits DB. For a per-host
  single-instance friends panel, negligible. If the host ever scales to many users →
  reconsider a dedicated cache (Redis) — but that contradicts the per-host topology anyway.
- **Refresh wire change scope:** scope 1 adds `expiresAt` to `RefreshResponse` (additive);
  refresh rotation (later) would add a `refresh` field and the SPA must store it → a true
  breaking change, deferred to a later increment.
- **5s revocation lag** is the accepted trade (D2). If a later need emerges for true
  sub-second disconnect, switch the cache off (per-request DB read) — heavier, flagged, not
  recommended now.
- **Admin cross-user revoke** is substantial power — an admin can lock a user out of the
  panel entirely. Explicit user decision (D4); flagged in the lock-doc update for any later
  reversal.
- **No XSS/CSRF scope increase** — sessions are bearer-validated; the existing `state` cookie
  CSRF posture and `SameSite=Lax` are unchanged.
- **Refresh-rotation/reuse-detection (D9)** — the most-cited "real" session security feature
  we haven't built. Documented as a deferred increment; a later milestone re-opens `RefreshResponse`.
- **Discord role recheck at refresh** — orthogonal; not fixed by the registry. A `forceReauth`
  flag could (later) trigger a full OAuth bounce on role change. Out of scope here.
- **In-flight-token clean break (D10)** is the only *breaking* change at deploy time. On a
  live host: every existing refresh token dies at deploy (≤15-min access-TTL ceiling), all
  users force-relogin. **Smoke a forced-relogin before any live redeploy.** Trusted-friends
  host — acceptable; flagged for announcement.
- **`EnsureCreated` wipe (D11)** — at close-out, dev DBs recreate empty (audit table lost
  — acceptable in dev).

---

## 7 · Cross-cutting (the contract-freeze checklist)

Each of these lands in Increment 9 unless noted otherwise:

- [ ] `Services/Auth/CLAUDE.md` lock-doc edit (the milestone re-opens it; the doc must update
      or self-contradict).
- [ ] Root `CLAUDE.md` M4 status line extension.
- [ ] `PLAN.md §4` — the `### M4·c` milestone entry.
- [ ] `PLAN.md §6` — frozen contract row for: `/auth/sessions`, the revoke endpoints,
      `/me.recentLogins`, `CallbackResult.*expiresAt`, `RefreshResponse.expiresAt`, the
      `auth.session.*` audit actions, the **D10 clean break** (explicitly).
- [ ] `appsettings.json` — the four new `KGSM_API_SESSIONS_*` keys documented with defaults.
- [ ] `Api.csproj` — `<Version>` 0.1.0 → 0.2.0; tag `v0.2.0` at release.
- [ ] `CHANGELOG.md ## [Unreleased]` — single milestone entry.

**Invariants check (re-stated):** #1 honest (UA from header; expiry from just-minted
token; revocation from real state) ✓ #4 additive-only in `/api/v1` ✓ #5 audit still
single-writer; new actions additive to the vocabulary, still direct-write, no double-write ✓.

---

## 8 · Self-validation plan (the test surface — `tests/Api.Tests/`, WebApplicationFactory +
faked Discord seam)

The full matrix lives in xUnit (smoke can't drive a real OAuth bounce). Each lines up with
an increment:

1. **Login creates a session row** (Increment 3): fake resolver → callback → assert row +
   linked audit `Meta["sid"]`.
2. **Per-request check** (Increment 4): valid → 200; revoked → 401 within cache window;
   expired → 401 even if `!Revoked`; pre-`sid` token → 401 "no session id"; disabled
   switch bypasses.
3. **Cache probe** (Increment 4): two requests within 5s → one DB hit; evict-on-revoke →
   next request fails immediately (assert <5s).
4. **Self-revoke + self-revoke-all** (Increment 6): the `sid`'s next access 401s; sibling
   sessions unaffected (or all die for `all:true`); audit rows land with the right action +
   severity + Meta.
5. **Admin cross-user revoke** (Increment 6): admin 200, the target's next access 401; viewer
   attempting it 403; audit `auth.session.revoke.admin` warn.
6. **Logout actually revokes** (Increment 5): the calling session row `Revoked=true`;
   subsequent bearer 401s; the audit row lands `auth.logout` info with `sid` Meta.
7. **Refresh honors session** (Increment 5): valid sid → new access (same sid) + `LastSeen`
   advanced; revoked sid → 401; expired session → 401.
8. **`GET /auth/sessions`** (Increment 6): only active rows (`!Revoked && Expires>now`);
   `current` flag set on the calling sid; admin `?userId=` returns another user's active
   set.
9. **GC worker** (Increment 8): expired rows deleted within ~one (shortened) interval;
   in-window rows survive.
10. **Carried-over Group E** (Increment 7): `CallbackResult.*expiresAt` present on success,
    absent on denied; `RefreshResponse.expiresAt` present ≈ now + 15min; `/me.recentLogins`
    ≥1 entry with matching UA after fake login, `[]` for a fresh actor.

Live validation (the trusted host): the full OAuth round-trip + a real device's UA in the
session row + a real self-revoke-all forcing a relogin. Records in `PLAN.md §8` at close-out.

---

## 9 · Deployment / migration notes

- **Existing prod DB (`/var/lib/kgsm-api/kgsm-api.db`) — one-shot table creation (D11):**
  run the `sqlite3` table-creation command once in place before the first deploy of the new
  code. DDL is the §5 schema block (the `INTEGER`-ticks shape, the two indexes); it mirrors
  what `EnsureCreated` would emit on a fresh DB. Milliseconds, no API downtime required, audit
  rows untouched. The command is **not kept** in the repo — it runs once and that's it. After
  the table exists, the new code assumes it.
- **Fresh DBs (new hosts, dev wipes):** `EnsureCreated` creates the `sessions` table
  automatically along with the rest (it's registered in `OnModelCreating`). No second step.
- **In-flight tokens break (D10):** existing refresh tokens bear no `sid` claim → the
  per-request check fails them → 401 → SPA bounces to login. This is the *intended* clean
  break. For the trusted-friends host: announce a relogin. **Smoke a forced-relogin before
  any live redeploy.**
- **Stable signing key still required** — the 30d refresh still hinges on
  `Api__SigningKey` being stable. Unchanged by this milestone.
- **No kgsm-lib bump** — this is entirely within the API; no engine contract change.
- **`Migrations/` directory:** do NOT introduce without re-deciding the migration lock (per
  the root `CLAUDE.md` gotcha). `EnsureCreated` for fresh DBs + the one-shot prod command for
  the existing DB is the chosen posture.

---

## 10 · Recommendation in one line (the locked shape)

**The API owns a per-row session registry keyed by a stable `sid` claim — checked per-request
through a 5s in-memory cache (revocation ≤5s; access-token disconnect ≤15min hard ceiling),
soft-deleted on revoke / hard-deleted when expired by a 10-min GC worker, exposed through
viewer-self `GET /auth/sessions` + self-revoke + admin cross-user revoke + `POST /auth/logout`
that finally revokes server-side — not a sliding-window refresh, not a per-request DB hit
without cache, not a user profile row (the user-row half of the lock stays locked), not a
grandfather path for pre-`sid` tokens.** Build it Increment 1 → 9; keep §0 current.