# CLAUDE.md — Services/Auth/

Auth on a node that **signs nobody in**. Every session this API accepts was minted by the cluster's
auth anchor (`kgsm-auth-anchor`) — which on a machine that founded its own cluster runs beside it — and
is verified here against the key that member publishes. **What a caller may do** is evaluated per
action, from this node's replica of the cluster's authority (accounts, roles, permissions, scoped
assignments, the catalog), on every request. The authority for the model is
`../../../../kgsm-docs/plans/permissions.md`, `../../../../cluster-auth-plan.md` and
`../../../../hosted-sign-in-plan.md`; this file is the local "what you must not break."

## Locked decisions (do not relitigate)

- **This node holds no key and mints nothing.** There is no sign-in door, no registration, no identity
  linking, no session registry, no signing key and no first-Owner bootstrap here. All of it is the
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
- **This API owns the machine's replica.** `OwnedReplicaFile` at `Api__UsersDbPath`
  (`/var/lib/kgsm/auth/users.db`) creates the file, sets aside one written at an older schema, and
  `AddAuthorityReplica()` applies what the anchor publishes and takes its snapshot into it; the leaves on
  this machine read the same file. The anchor's own store is a different file in its own state
  directory, so a snapshot never rewrites the authority it came from. Nothing here writes a record of
  its own: a write would land unversioned and be overwritten by the next change the anchor publishes.
- **The account is resolved per request; nothing about access is read off the token.** `LiveAuthority`
  runs on the JwtBearer `OnTokenValidated` event, resolves the session's identity to an account in the
  replica (`MemberAccess`), and stamps its id as the `kgsm_account` claim. Three outcomes, kept apart:
  a **disabled** account fails authentication; an identity with **no account** is a stranger — the
  session stands, nothing is stamped, and every gate refuses it; an **unreadable replica** answers
  `502 authority_unavailable` via `OnChallenge` — never a `401` and never a default grant.
- **Every gate names one action.** `[RequiresAction(ActionIds.X)]` is an `[Authorize]` whose policy
  (`action:<id>`, built on demand by `ActionPolicyProvider`) is decided by `ActionAuthorizationHandler`:
  the caller's account, evaluated from the replica at the target the route names — a server route
  (`api/v1/servers/{id}`) at that server's install (`instance:<node>/<id>#<nonce>`, or this node while
  the nonce is unknown), everything else at this node; a cluster-scoped action widens to the cluster on
  its own. Where the action depends on the request — a command's verb, a settings patch touching
  maintenance windows, a leaf's configuration — the endpoint declares every action it can decide with
  an `OperationActionsAttribute` (`[ActionByVerb]`, `[ActionWhenPresent]`, `[LeafConfigAction]`), whose
  entries come from the function the handler checks with (`ActionIds.ForVerb`, `ActionIds.ConfigAction`,
  the attribute itself), and the controller asks `NodeAccess` in the body. A batch's members are each
  refused on their own. `ActionIds` names every id this API gates on; `api:*` are declared in
  `ApiActionDeclarations.cs`, every other one in its own component's manifest.
- **What is enforced is published.** `GET /api/v1/operations` (`ApiOperations`) is built from those
  attributes on the endpoints as mapped — method, route under `/api/v1`, action, and scope by
  `NodeAccess.IsServerRoute`, the same test `TargetOf` evaluates with — so a client gates a control on
  the request it will make and holds no action ids. Every entry a request matches must be held. A gate
  decided in a handler from anything other than those attributes is a gate no client can see, and
  `OperationsTests` fails on any `[RequiresAction]` route the document leaves out.
- **A collection is cut to its reader.** `GET /servers`, availability, and every server frame on the
  stream carry only the servers the caller may read (`NodeAccess.AllowedServersAsync`), so somebody
  granted one server sees that one server. A job is read at the server it acted on and is a `404`
  otherwise, so an id says nothing about servers the caller cannot see.
- **What the engine is called for, the code names.** The action generator requires every kgsm-lib
  engine call to be either required by this API's own service account (the assembly-level `[Requires]`
  in `ApiActionDeclarations.cs`: the reads behind every page and the stream) or named by the code making
  it for a checked person — the route's `[RequiresAction]`, or `[PerformedFor]` on the service below the
  gate. The build fails on a call that is neither.
- **Credentials handed out ahead of use are bound and re-evaluated.** A backup download ticket carries
  the account, the action (`kgsm:server.files.read`) and the server, and `RedeemAsync` asks the replica
  again before anything is spent. A push button's staged row names the person, the operation and its
  target, and `NotificationActionsController` evaluates that person for that operation's action at the
  tap.
- **An identity names its provider.** `KgsmIdentity.Handle` (`provider:subject`) is the token subject
  and the `userId` on the wire, built by the identity, never interpolated at a call site.
- **Another member acting for a person is a separate scheme.** A request carrying the acting header is
  routed to `MemberActingHandler`, which checks the member's service token (signed with the cluster
  secret) and resolves the named person — or one of the member's own service accounts — against this
  node's replica (`MemberActingAccountResolver`), stamping the same `kgsm_account` claim. The routing is
  by header, not by trying one scheme and falling back, because the two establish trust in unrelated
  ways.
- **`/.well-known/oauth-protected-resource` names the provider** (RFC 9728): the issuer this node
  verifies against, read through the holder, and only when it is a URL — `503 no_issuer` otherwise.
  Anonymous and readable from any origin without credentials, by its own route-level CORS policy,
  because a panel served with no member behind it asks whichever member a person names.
- **The panel this node serves is a client of the provider, by announcement.** When the bundle is in
  the web root, this node publishes `auth.client` (`ClusterClientAnnouncement.ControlPanel`: paths only,
  `/signed-in` and `/` — the statement the anchor declares a static-host panel with),
  which the provider joins to the browser address the roster hands out for this node. A node serving
  no panel announces nothing.
- **This node writes the host file its machine's leaves verify against** (`HostProviderFileWriter`,
  `Api__HostProviderFilePath`, `/var/lib/kgsm/cluster/auth-provider.json`), from its own read through the
  holder. It is the one writer on the machine.
- **It introduces itself to the anchor beside it only on the machine that founded the cluster**, and
  only while it knows no member (`LocalAnchorJoin`, `ClusterFounding.IsFoundedHere`). A machine holding
  another cluster's secret is joined by an Owner; the anchor beside it holds nothing there.
- **There is no `/auth` path here at all.** A surface finds the provider from the protected-resource
  document above; an `/auth/*` request is an ordinary `404`, never the SPA's HTML.
- **CORS admits the provider's registered clients' origins** (`IClientOrigins`, read through the holder
  by `ClusterSessionKeys`), without credentials, so no node is configured with a list of origins. A host
  with auth disabled admits any origin: it has no session to protect.
- **Auth is ON by default.** `Api__AuthDisabled=true` swaps in `DisabledAuthHandler` (a synthetic Owner,
  attributed to `Api__DisabledAuthActor`): every action is allowed and `/me/access` reports an Owner
  holding every action this node's components declare. Loudly logged. Never enable it on an exposed host.

## Invariants when you touch this

- **Secure-by-default.** A `FallbackPolicy` requires an authenticated caller, so a **new endpoint is
  gated unless it opts out**, and a new endpoint names its action with `[RequiresAction]`. Adding an
  open endpoint is a deliberate, reviewed act — not an omission. `/health`, `/api/v1` and the
  protected-resource document carry `[AllowAnonymous]`, and **one anonymous write** needs its own
  paragraph: **`POST /notifications/actions/{handle}`**. A service worker holds no session, so a
  notification button has no bearer to present and the handle stands in for one. What keeps that sound
  is that the handle names an operation **staged server-side**, is **bound to the push endpoint** it was
  staged for, is **single-use with a short life**, and — the load-bearing one — **evaluates the person's
  access to the operation at redemption from the replica**, never from anything carried since staging.
  Every refusal about the handle is one `404` with one message, because separate answers let somebody
  probe which handles exist.
- **`401` vs `403` is the load-bearing split.** `401` = no/invalid bearer (challenge); `403` =
  authenticated, action not held (forbid). Self-service surfaces — `/me`, `/me/access`,
  `/me/preferences`, `/stream` — are `[Authorize]` alone, so somebody awaiting approval reads their own
  standing.
- **Honest failure modes:** the replica unreadable → `502 authority_unavailable`. **Never a default
  grant, and never a denial either** — "we could not ask" is a third answer and must stay one. A
  disabled account → terminal refusal.
- **`/stream` gates per topic and per frame, not per endpoint.** Any authenticated caller connects; each
  topic delivers only what the reader's access reaches (`StreamProtocol.Gate`) — silently, never a 403
  on the whole stream. Access is evaluated on every frame from the replica as it stands
  (`Realtime/StreamAccess.cs`), so access granted or taken away while a stream is open applies to the
  next frame, both ways. `me` needs nothing, so somebody awaiting approval holds a connection that
  carries news about their own account and nothing else.
- **The SSE bearer is a normal `Authorization` header**, so `/stream` authenticates through the standard
  JwtBearer pipeline like every other request — a query-string token authenticates nothing
  (`Stream_Sse_QueryTokenIgnored`).
