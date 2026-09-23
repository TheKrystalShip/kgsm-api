# CLAUDE.md — Services/Auth/

Auth on a node that **signs nobody in**. Every session this API accepts was minted by the cluster's
auth anchor (`kgsm-auth-anchor`) — which on a machine that founded its own cluster runs beside it — and
is verified here against the key that member publishes. **Authority** is the tier on the caller's KGSM
account, read from this node's replica of the cluster's accounts on every request. The authority for
the model is `../../../../cluster-auth-plan.md` and `../../../../hosted-sign-in-plan.md`; this file is
the local "what you must not break."

## Locked decisions (do not relitigate)

- **This node holds no key and mints nothing.** There is no sign-in door, no registration, no identity
  linking, no session registry, no signing key and no first-admin bootstrap here. All of it is the
  anchor's. A bearer is accepted only through `ClusterSessionValidation.Accepting(IClusterSessionKeys)`:
  ES256, the published key matched exactly on its `kid`, the cluster's audience, the anchor's issuer.
  A symmetric token is refused outright, because this node has no business holding a key to check one.
- **The keys are read through the holder of `auth`.** `ClusterSessionKeys` resolves the published key,
  audience and issuer from gossip, through whichever member the assignment names, at the gossip
  cadence — so a reassignment or a rotation needs no restart. Until gossip has named a holder there is
  nothing to verify against, and every session is refused; `AuthAnchorReport` says so in this service's
  log, once and whenever it changes, because from a browser a missing anchor reads exactly like a wrong
  password.
- **A session has no row here; an ended one does.** The check on every request is a deny-list:
  `session.revoke` arrives over the bus, `EndedSessionStore` records it (its own `ended_sessions`
  table, created with `CREATE TABLE IF NOT EXISTS` beside `EnsureCreated`), and
  `ClusterSessionRevocations` caches the answer on the request path with `Api__SessionsCacheTtlMs` as
  the lag. A token with no `sid` is refused — a session nothing could end is not one this node holds
  open. A refresh token is never a bearer; it is spent at the anchor.
- **An already-open SSE stream is covered too** — it re-asks the deny-list every 20s and ends itself
  when its `sid` has been ended (`Realtime/StreamConnection.cs`). The re-check is on the SESSION, not on
  the access token's expiry: a token lapses every ~15 min by design and the client renews it at the
  anchor, so ending a stream on that would cost the panel a visible reconnect four times an hour.
- **Authority is resolved per request, from the replica, and the `tier` claim is not trusted.**
  `LiveAuthority` runs on the JwtBearer `OnTokenValidated` event and replaces the minted claim with
  what the account says today, so disable, demote and revoke are one mechanism: the anchor changes the
  record, replication delivers it, and the next request reads it. `Api__AuthorityCacheSeconds`
  (default 5) is the demotion lag. Three outcomes, kept apart: a **disabled** account fails
  authentication; an identity with **no account** is a stranger holding `none` (the session stands and
  every gate refuses it); an **unreadable replica** answers `502 authority_unavailable` via
  `OnChallenge` — never a `401` and never the token's own tier.
- **A replicated change reaches open streams at once.** `AccountChangesReachOpenStreams` and
  `AccountRemovalsReachOpenStreams` wrap the shared replication handlers: once the replica has applied a
  change they drop the account's cached authority answers (`UserDirectory.ReloadAsync`) and call
  `StreamHub.AuthorityChanged` with what the replica now holds — never with what the message said,
  because a redelivery can be older than what is held. The stream's own re-read is the backstop.
- **`UserDirectory` is the replica, read-only.** It exposes the authority resolution, the replica
  handle replication applies to, and the member-acting store — never a write. The anchor is the only
  writer of accounts; a write here would land unversioned and be overwritten by the next change the
  anchor publishes.
- **The tier vocabulary is the ecosystem's.** `KgsmTier`/`KgsmTiers`/`KgsmAuthClaims`/`KgsmTokenKind`
  come from `TheKrystalShip.KGSM.Auth`; this project keeps only `AuthPolicy` (ASP.NET policy names)
  and `TierAuthorizationHandler` (how this surface enforces them).
- **An identity names its provider.** `KgsmIdentity.Handle` (`provider:subject`) is the token subject
  and the `userId` on the wire, built by the identity, never interpolated at a call site.
- **Another member acting for a person is a separate scheme.** A request carrying the acting header is
  routed to `MemberActingHandler`, which checks the member's service token (signed with the cluster
  secret) and resolves the named person against this node's own replica. The routing is by header, not
  by trying one scheme and falling back, because the two establish trust in unrelated ways.
- **`/auth/*` says which member signs people in.** `SignInElsewhereController` answers every `/auth`
  path `503` with the holder's **name** on `X-Kgsm-Auth-Holder` (`auth_held_by_anchor`), or
  `auth_holder_unknown` when none is known — never an address, and never the SPA's HTML.
- **Auth is ON by default.** `Api__AuthDisabled=true` swaps in `DisabledAuthHandler` (synthetic admin,
  attributed to `Api__DisabledAuthActor`), loudly logged. Never enable it on an exposed host.

## Invariants when you touch this

- **Secure-by-default.** A `FallbackPolicy` requires an authenticated caller, so a **new endpoint is
  gated unless it opts out**. Adding an open endpoint is a deliberate, reviewed act — not an omission.
  `/health`, `/api/v1` and the `/auth/*` refusal carry `[AllowAnonymous]`, and **one anonymous write**
  needs its own paragraph: **`POST /notifications/actions/{handle}`**. A service worker holds no
  session, so a notification button has no bearer to present and the handle stands in for one. What
  keeps that sound is that the handle names an operation **staged server-side**, is **bound to the push
  endpoint** it was staged for, is **single-use with a short life**, and — the load-bearing one —
  **resolves the tier at redemption from the replica**, never from anything carried since staging.
  Every refusal about the handle is one `404` with one message, because separate answers let somebody
  probe which handles exist.
- **Tier gating** (hierarchical: admin ⊇ operator ⊇ viewer): viewer = reads and the `/stream` topics that
  carry them, operator = the command `POST`, admin = diagnostics + reserved (settings/install/audit-config).
  `401` = no/invalid bearer (challenge); `403` = authenticated, tier too low (forbid) — keep that split.
- **Honest failure modes:** the replica unreadable → `502 authority_unavailable`. **Never a default
  grant, and never a denial either** — "we could not ask" is a third answer and must stay one. A
  disabled account → terminal refusal; a tier is **never** silently softened to make a request work.
- **`/stream` gates per topic, not per endpoint.** Any authenticated caller connects and keeps only the
  topics their tier reaches (`StreamProtocol.MinimumTier`) — dropped silently, never a 403 on the whole
  stream. The floor is `me`, which needs no grant, so somebody awaiting approval holds a connection that
  carries news about their own account and nothing else. A tier changed while a stream is open is
  applied to the live connection; it only ever takes reach away — a promotion adds no subscription back.
- **The SSE bearer is a normal `Authorization` header**, so `/stream` authenticates through the standard
  JwtBearer pipeline like every other request — a query-string token authenticates nothing
  (`Stream_Sse_QueryTokenIgnored`).
