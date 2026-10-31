# CLAUDE.md — Services/Auth/

Auth on a node that **signs nobody in**. A person presents the access token the sign-in provider issued
(`tks-auth`, found through `Sessions__Issuer`); this API checks it against the provider's published keys and
reads what the person may do from the token's actions. Nothing about a person is stored, copied or
replicated here. The authority for the model is `../../../../../kgsm-docs/systems/authorization/` and
`../../../../../kgsm-docs/systems/identity/`; this file is the local "what you must not break."

## Locked decisions (do not relitigate)

- **This node holds no key and mints nothing.** There is no sign-in door, no registration, no session
  registry, no signing key. A bearer is accepted through `TheKrystalShip.KGSM.Sessions` (`AddKgsmSessions`):
  ES256, the key found by discovery and JWKS, the configured issuer and audience, access-token type, a
  30-second skew. Anything else is a `401`.
- **A token lists actions; a grant is cluster-wide.** What a caller may do is the token's `tks_actions`
  (the claim name is `Sessions__ActionsClaim`). There are no node or server scopes: `NodeAccess` answers
  `Allows(user, action)`, and a collection is all of its servers or none. A role change reaches a person at
  their next token renewal, which is the bound on how long a removed role keeps working.
- **Every gate names one action.** `[RequiresAction(ActionIds.X)]` is an `[Authorize]` whose policy
  (`action:<id>`, built on demand by `ActionPolicyProvider`) carries the sessions package's
  `ActionRequirement`, authenticated through the caller scheme. A person is admitted by the package's
  handler; `TrustedCallerHandler` admits a member acting as itself and the open caller of an auth-disabled
  host. Where the action depends on the request (a command's verb, a settings patch touching maintenance
  windows, a leaf's configuration) the endpoint declares every action it can decide with an
  `OperationActionsAttribute` (`[ActionByVerb]`, `[ActionWhenPresent]`, `[LeafConfigAction]`), whose entries
  come from the function the handler checks with, and the controller asks `NodeAccess` in the body. A
  batch's members are each refused on their own. `ActionIds` names every id this API gates on; `api:*` are
  declared in `ApiActionDeclarations.cs`, every other one in its own component's manifest.
- **What is enforced is published.** `GET /api/v1/operations` (`ApiOperations`,
  `../../../../../kgsm-docs/reference/operation-manifest.md`) is built from those attributes on the
  endpoints as mapped, every entry at the `cluster` scope. A gate decided in a handler from anything other
  than those attributes is a gate no client can see, and `OperationsTests` fails on any `[RequiresAction]`
  route the document leaves out.
- **One door, three callers.** `MemberAuthentication.Caller` is a policy scheme that sends each request to
  the handler that can answer it, decided from the request and never by trying one and falling back:
  - a **person**: `Authorization: Bearer <access token>`, signed ES256, through the sessions package;
  - **another member acting as itself**: `Authorization: Bearer <cluster service token>`, signed HS256 with
    the cluster secret and minted by `IClusterTokenService`. `MemberAuthenticationHandler` validates it and
    the member gate exactly as `ClusterRequest` does, and the member is fully trusted;
  - **an auth-disabled host**: `DisabledAuthHandler`, a synthetic Owner (below).

  A member acting **for** a person forwards that person's own access token, which is a person's like any
  other. The signing algorithm in the token header tells the first two apart.
- **With no issuer, the node answers its own machine only.** `Sessions__Issuer` blank skips
  `AddKgsmSessions` and a middleware refuses any non-loopback caller (after the forwarded headers, so a
  stranger a local proxy relays is refused). A person cannot authenticate then; a member can, from this
  machine.
- **Credentials handed out ahead of use are short-lived, not re-evaluated.** A backup download ticket and a
  transfer ticket are bound to what they authorise and expire quickly. A notification button's handle names
  the person, and `NotificationActors` remembers what that person's token last listed: trusted for
  `NotificationActors.Freshness` to decide who is told, and for the tighter `ActionFreshness` (a
  ceiling above a button's own life) to honour a tap, which carries no token of its own.
- **A person is named by their token.** `CallerId` reads `sub` (the key records hold), `name` (stored beside
  the subject wherever a record keeps only a subject) and the audit actor (`local:<name>` for a person,
  `system:<member>` for a member). A record written for a person never asks the provider who they are.
- **A leaf is asked on the person's own token.** `LeafSurfaceRelay`, `SchedulerClient` and `ReactorClient`
  forward the person's `Authorization` header (`CallerId.PersonToken`), so the leaf admits them itself and
  records the token's subject and name as the author of an automation. A caller who is not a person
  forwards nothing.
- **Windows are checked when saved.** `ServerSettingsController` refuses a maintenance-window save unless
  the saver's token lists every action `MaintenanceWindowActions` derives from the windows' tasks; the
  scheduler then runs them as itself, attributed to the subject passed as the engine author and shown
  under the token's display name passed beside it.
- **Streams end with the token.** The SSE loop's cancellation is `KgsmPerson.UntilExpiry`; the client
  reconnects with a fresh token (`Realtime/CLAUDE.md`).
- **`/.well-known/oauth-protected-resource` names the provider** (RFC 9728): `Sessions__Issuer` when it is a
  URL, `503 no_issuer` otherwise. Anonymous and readable from any origin without credentials, by its own
  route-level CORS policy, because a panel served with no member behind it asks whichever member a person
  names.
- **`/.well-known/tks-actions.json` is the node's action manifest** (`NodeActionManifest`), anonymous: the
  engine's, every leaf's and every co-located anchor's manifests, one per component, flattened to the
  application format (scope `cluster`, no requirements), without the provider's own `auth:*`.
- **There is no `/auth` path here at all.** A surface finds the provider from the protected-resource
  document above; an `/auth/*` request is an ordinary `404`, never the SPA's HTML.
- **CORS admits the origins in `Api__CorsOrigins`**, without credentials. A host with auth disabled admits
  any origin: it has no session to protect.
- **Auth is ON by default.** `Api__AuthDisabled=true` routes every request to `DisabledAuthHandler` (a
  synthetic Owner, attributed to `Api__DisabledAuthActor`): every action is allowed and `/me/access` reports
  an Owner holding every action this node's components declare. Loudly logged. Never enable it on an
  exposed host.

## Invariants when you touch this

- **Secure-by-default.** A `FallbackPolicy` requires an authenticated caller, so a **new endpoint is gated
  unless it opts out**, and a new endpoint names its action with `[RequiresAction]`. Adding an open
  endpoint is a deliberate, reviewed act — not an omission. `/health`, `/api/v1` and the protected-resource
  document carry `[AllowAnonymous]`, and **one anonymous write** needs its own paragraph:
  **`POST /api/v1/notifications/actions/{handle}`**, mapped by the notification library. A service worker
  holds no session, so a notification button has no bearer to present and the handle stands in for one. What
  keeps that sound is that the handle names an operation **held server-side**, is **minted per device**, is
  **single-use with a short life**, and — the load-bearing one — **`FleetNotificationActions` checks the
  person's access to the operation at redemption** against what their token last listed, within
  `NotificationActors.ActionFreshness`. Every refusal answers `200 { ok: false, message }`, and a handle that is
  unknown, spent or expired says one thing, so separate answers cannot be used to probe which handles
  exist. A person's own notification routes sit under `/api/v1/me` on `api:push.devices`, the self action
  every signed-in person holds.
- **`401` vs `403` is the load-bearing split.** `401` = no/invalid bearer (challenge); `403` =
  authenticated, action not held (forbid), whose envelope says `This needs <action>.`
  (`ActionRefusalResultHandler`), as every member's refusal does. Self-service surfaces — `/me`, `/me/access`,
  `/me/preferences`, `/stream` — are `[Authorize]` alone, so somebody whose token lists nothing reads their
  own standing.
- **`/stream` gates per topic and per frame, not per endpoint**, and its bearer is a normal `Authorization`
  header: `Realtime/CLAUDE.md` (Auth).
