using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Json;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Cluster;

using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Discord;

using TheKrystalShip.KGSM.Auth.Sessions;
using TheKrystalShip.KGSM.Auth.Users;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// Auth — per-host, Model A (architecture.html §3·f, keystone O5). Identity is a global SSO anchor at
/// whichever provider this host signs people in through; authorization is a short-lived host-scoped
/// JWT this host mints after verifying identity once (the provider's token is then discarded) and
/// resolving the tier. The two halves are separate seams — <see cref="SignInService"/> composes an
/// identity provider with an authority provider — so this controller names neither and works the same
/// whichever pair a host is wired with. Stateless — no user row (the M4 bearer decision).
/// <para>
/// The mint/refresh/session/logout machinery and the verdict logic sit entirely above the sign-in
/// seams, so the whole 401/403/tier matrix is exercised in-process against a fake and no test needs
/// to reach a provider. The login endpoints answer <c>503</c> until this host is configured with an
/// application, a role-lookup credential and a role map.
/// </para>
/// </summary>
[ApiController]
public sealed class AuthController(
    ISignInService signIn,
    UserDirectory users,
    ISessionTokenService tokens,
    SessionStore sessions,
    ISessionValidator sessionValidator,
    ApiOptions options,
    AuditService audit,
    IClusterTokenService clusterTokens,
    IClusterPeerGate peerGate,
    PeersStore peers,
    IHttpClientFactory httpClientFactory,
    ILogger<AuthController> logger) : ControllerBase
{
    // Outbound JSON for the vouch relay to a peer's ClusterSession receiver — camelCase like every
    // other cluster HTTP call (mirrors PeersController/OutboxDrainer/GossipWorker's identical
    // BuildJsonOptions pattern; built once, not per-request).
    private static readonly JsonSerializerOptions VouchJsonOptions = BuildVouchJsonOptions();

    private static JsonSerializerOptions BuildVouchJsonOptions()
    {
        var jsonOptions = new JsonSerializerOptions();
        ApiJson.Configure(jsonOptions);
        return jsonOptions;
    }

    // The in-flight login cookie — set at /start, consumed at /callback. It carries BOTH halves of
    // the handshake: the CSRF `state` Discord echoes back, and the PKCE `code_verifier` presented at
    // the exchange. Keeping the verifier here rather than in a server-side pending store is what lets
    // this API run PKCE while staying stateless. HttpOnly and our origin, so only this browser's own
    // login can satisfy it — the binding is the whole CSRF property, and a set of issued states held
    // server-side would not have it. One-time, short-lived.
    private const string StateCookie = "kgsm_oauth_state";
    private static readonly TimeSpan StateTtl = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Begin the OAuth bounce — 302 to Discord's authorize URL (the API owns client id / redirect /
    /// scopes). <c>prompt=none</c> is silent SSO; the client retries with <c>consent</c> on
    /// <c>login_required</c>. Sets the one-time CSRF state cookie verified at the callback. 503 until
    /// Discord is configured (M4·b).
    /// </summary>
    [AllowAnonymous]
    [HttpGet("/auth/discord/start")]
    public IActionResult Start([FromQuery] string? prompt)
    {
        if (!options.DiscordConfigured)
            return Error(StatusCodes.Status503ServiceUnavailable, "auth_unconfigured",
                "Discord auth is not configured on this host.");

        OAuthHandshake handshake = OAuthHandshake.Create();
        Response.Cookies.Append(StateCookie, handshake.ToCookieValue(), StateCookieOptions());
        // Only the challenge travels — the verifier stays in the cookie, never in a URL.
        string url = signIn.BuildAuthorizeUrl(handshake.State, handshake.CodeChallenge, prompt ?? "none");
        return Redirect(url);
    }

    /// <summary>
    /// The OAuth landing — exchange the code, verify identity, resolve the tier, mint the bearer.
    /// <list type="bullet">
    /// <item><c>200</c> <c>{ verdict:"ok", tier, token, refresh, userId }</c> — a session on the account
    /// this identity proves. <c>tier:"none"</c> is the honest shape for an account still awaiting
    /// approval: it authenticates, and it may do nothing yet.</item>
    /// <item><c>403</c> <c>{ verdict:"denied", userId }</c> — identity verified, account switched off
    /// on this host (terminal).</item>
    /// <item><c>400</c> — bad/forged state (<c>invalid_state</c>) or missing code · <c>401</c> — bad/expired code · <c>502</c> — Discord or the account store unreachable · <c>503</c> — unconfigured, or already holding as many unapproved accounts as this host will.</item>
    /// </list>
    /// </summary>
    [AllowAnonymous]
    [HttpGet("/auth/discord/callback")]
    public async Task<IActionResult> Callback([FromQuery] string? code, [FromQuery] string? state, CancellationToken ct)
    {
        if (!options.DiscordConfigured)
            return Fail(StatusCodes.Status503ServiceUnavailable, "auth_unconfigured",
                "Discord auth is not configured on this host.");

        // CSRF gate: the state Discord echoes back must equal the one issued to THIS browser. The
        // cookie is one-time — clear it whatever the outcome (no replay). A missing cookie
        // (expired/never-started), a malformed one, or a mismatch is a forged or stale login -> 400,
        // never a grant. This runs BEFORE any exchange, so the redirect handoff never weakens it.
        string? cookie = Request.Cookies[StateCookie];
        if (cookie is not null)
            Response.Cookies.Delete(StateCookie, StateCookieOptions());
        if (!OAuthHandshake.TryParse(cookie, out OAuthHandshake handshake) || !handshake.MatchesState(state))
            return Fail(StatusCodes.Status400BadRequest, "invalid_state",
                "the OAuth state did not validate (possible CSRF, or the login expired — start again).");

        if (string.IsNullOrWhiteSpace(code))
            return Fail(StatusCodes.Status400BadRequest, "bad_request", "missing authorization code");

        ResolvedPrincipal? resolved;
        try
        {
            resolved = await signIn.ResolveAsync(code, handshake.CodeVerifier, ct);
        }
        catch (KgsmAuthProviderException ex)
        {
            // Couldn't reach/parse the provider — an honest upstream error, NEVER a default grant.
            // Caught as the neutral type: whichever half of the sign-in failed, identity or authority,
            // the answer to the browser is the same and this API has no verdict to report.
            logger.LogWarning(ex, "{Provider} auth exchange failed.", signIn.Provider);
            return Fail(StatusCodes.Status502BadGateway, "auth_provider_error",
                "Could not complete authentication with the identity provider.");
        }

        // The code couldn't be exchanged into a verified identity (bad/expired/reused).
        if (resolved is null)
            return Fail(StatusCodes.Status401Unauthorized, "login_required",
                "The authorization code was invalid or expired.");

        string userHandle = resolved.Identity.Handle;

        // A verified identity is not yet a user. It proves an account here or it proves none, and
        // when it proves none an unapproved one is created for it to prove — with no tier, so the
        // session it gets can read who it is and nothing else. Membership of a guild is not consulted
        // and contributes nothing: what someone may do is on their account and on nothing else.
        if (!users.Available)
            return Fail(StatusCodes.Status502BadGateway, "authority_unavailable",
                users.UnavailableReason ?? "The KGSM account store is unavailable on this host.");

        LinkResult link;
        try
        {
            link = await users.Linking.ResolveOrProvisionAsync(
                resolved.Identity, DateTimeOffset.UtcNow, options.PendingPolicy, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not resolve {Handle} against the KGSM account store.", userHandle);
            return Fail(StatusCodes.Status502BadGateway, "authority_unavailable",
                "The KGSM account store could not be read.");
        }

        if (link.Outcome == LinkOutcome.PendingCapReached)
        {
            // Not a denial of this person — a refusal to hold more unapproved accounts. Logged so an
            // admin can see it happening, because from the outside it is indistinguishable from being
            // turned away.
            logger.LogWarning(
                "{Handle} signed in but this host is already holding {Cap} accounts awaiting approval.",
                userHandle, options.PendingUserCap);
            return Fail(StatusCodes.Status503ServiceUnavailable, "not_accepting_accounts",
                "This host is not accepting new accounts right now. Ask an administrator.");
        }

        KgsmUser account = link.User!;
        if (link.Outcome == LinkOutcome.Provisioned)
        {
            await RecordUserAsync(AuditAction.UserProvision, resolved.Identity, account,
                $"{account.DisplayName} signed in for the first time and is awaiting approval", ct);
        }

        // Verified identity, and this host has switched the account off -> terminal 403, the same
        // shape a denial has always had.
        if (account.Status == UserStatus.Disabled)
            return options.FrontendRedirectEnabled
                ? FrontendRedirect(Frag(("error", "denied")))
                : StatusCode(StatusCodes.Status403Forbidden,
                    // M4·c Increment 7: no tokens are minted on a denial, so both expiry fields stay
                    // null — WhenWritingNull omits them, keeping this branch's wire shape unchanged.
                    new CallbackResult("denied", null, null, null, userHandle, null, null));

        // An unapproved account gets a real session at `none`, deliberately. A bare 403 tells someone
        // who has just proved who they are nothing about what to do next; a session lets the panel
        // say they are waiting on an admin, and lets that admin see them on the accounts screen.
        KgsmTier tier = account.EffectiveTier;

        string? userAgent = UserAgent();
        MintedSession minted = await MintSessionAsync(resolved.Identity, tier, userAgent, ct);
        string sessionId = minted.SessionId;
        MintedToken access = minted.Access;
        MintedToken refresh = minted.Refresh;

        // M5: an auth.login is an API-internal action (no kgsm event), so it is written directly here
        // — no double-write risk. Best-effort: a failed audit write must never break the login. M4·c
        // adds the `sid` to Meta so a login event links to its session row (forensics: "this login
        // created session sid_X, which was later revoked"). M4·c Increment 7 (Group E #11) additionally
        // stamps `userAgent` (the same value just persisted on the session row above) so `/me`'s
        // recent-logins read can honestly label "which device" without a second UA capture — additive
        // to the existing direct-write, NOT a new writer (invariant #5).
        await RecordAuthAsync(AuditAction.AuthLogin, resolved.Identity, tier,
            $"{resolved.Identity.Display} logged in", sessionId, userAgent, ct);

        // SPA handoff (when a frontend URL is configured): 302 to the SPA with the tokens in the URL
        // FRAGMENT — never the query, so they don't reach access logs or the Referer header. The SPA
        // reads them, adopts the session, and strips the fragment. Otherwise return the JSON contract
        // (API-only deployments + the auth tests). The redirect target is the single configured URL.
        // M4·c Increment 7 (Group E #12): the JSON contract carries both tokens' mint-time expiry so
        // the SPA can schedule proactive refresh instead of reacting to a 401. Out of scope: the SPA
        // fragment-redirect handoff above is untouched — expiresAt is a JSON-contract-only addition
        // this increment (the fragment already omits `tier` too; adding query params there is a
        // separate, not-yet-needed contract change).
        return options.FrontendRedirectEnabled
            ? FrontendRedirect(Frag(("access", access.Token), ("refresh", refresh.Token)))
            : Ok(new CallbackResult("ok", KgsmTiers.ToWire(tier), access.Token, refresh.Token, userHandle,
                access.ExpiresAt, refresh.ExpiresAt));
    }

    /// <summary>
    /// <c>POST /auth/login</c> — sign in with a KGSM password. The door that needs no identity
    /// provider configured on this host at all, and the one an admin created at setup comes through.
    /// <list type="bullet">
    /// <item><c>200</c> <see cref="LoginResult"/> — a minted session, exactly as the OAuth callback
    /// hands one over. <c>status:"pending"</c> with <c>tier:"none"</c> is a real session for an
    /// account awaiting approval, not a failure.</item>
    /// <item><c>400</c> <c>bad_request</c> — no username or no password.</item>
    /// <item><c>401</c> <c>invalid_credentials</c> — wrong username <b>or</b> wrong password, one
    /// answer at one cost. Never tell a caller which.</item>
    /// <item><c>403</c> <c>account_disabled</c> — right password, account switched off.</item>
    /// <item><c>429</c> <c>too_many_attempts</c> + <c>Retry-After</c> — locked out.</item>
    /// <item><c>503</c> <c>users_unavailable</c> — the account store could not be opened.</item>
    /// </list>
    /// </summary>
    [AllowAnonymous]
    [HttpPost("/auth/login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest? body, CancellationToken ct)
    {
        if (!users.Available)
            return Error(StatusCodes.Status503ServiceUnavailable, "users_unavailable",
                users.UnavailableReason ?? "The KGSM account store is unavailable on this host.");

        if (body is null || string.IsNullOrWhiteSpace(body.Username) || string.IsNullOrWhiteSpace(body.Password))
            return Error(StatusCodes.Status400BadRequest, "bad_request",
                "username and password are required");

        DateTimeOffset now = DateTimeOffset.UtcNow;
        LocalSignInResult result;
        try
        {
            result = await users.SignIn.SignInAsync(body.Username, body.Password, now, ct);
        }
        catch (Exception ex)
        {
            // The store went away between startup and now. An outage, never a denial — the same rule
            // as an unreachable identity provider.
            logger.LogError(ex, "Local sign-in failed: the KGSM account store could not be read.");
            return Error(StatusCodes.Status503ServiceUnavailable, "users_unavailable",
                "The KGSM account store could not be read.");
        }

        switch (result.Outcome)
        {
            case LocalSignInOutcome.InvalidCredentials:
                // Logged with the username that was tried, so an operator can still see a run of
                // attempts against one name — the distinction the wire deliberately does not carry.
                logger.LogInformation("Local sign-in refused for '{Username}'.", body.Username);
                return Error(StatusCodes.Status401Unauthorized, "invalid_credentials",
                    "That username and password do not match an account here.");

            case LocalSignInOutcome.LockedOut:
                int seconds = Math.Max(1, (int)Math.Ceiling(((result.RetryAfter ?? now) - now).TotalSeconds));
                Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return Error(StatusCodes.Status429TooManyRequests, "too_many_attempts",
                    $"Too many failed attempts. Try again in {seconds}s.");

            case LocalSignInOutcome.Disabled:
                return Error(StatusCodes.Status403Forbidden, "account_disabled",
                    "That account is disabled on this host.");
        }

        ResolvedPrincipal principal = result.Principal!;
        string? userAgent = UserAgent();
        MintedSession minted = await MintSessionAsync(principal.Identity, principal.Tier, userAgent, ct);

        await RecordAuthAsync(AuditAction.AuthLogin, principal.Identity, principal.Tier,
            $"{principal.Identity.Display} signed in with a password", minted.SessionId, userAgent, ct);

        return Ok(new LoginResult(
            minted.Access.Token, minted.Refresh.Token, KgsmTiers.ToWire(principal.Tier),
            principal.Identity.Handle, UserStatuses.ToWire(result.User!.Status),
            minted.Access.ExpiresAt, minted.Refresh.ExpiresAt));
    }

    /// <summary>
    /// <c>POST /auth/cluster-session</c> — the cluster SSO vouch (<c>PLAN-peers.md</c>): a peer node
    /// presents a valid cluster service token and asserts an already-authenticated user's identity;
    /// this node mints its OWN native session for that user and returns the tokens — the SSO grant
    /// that lets a login on one node carry onto another. Cluster-token authed + disable-list gated,
    /// the same inline fail-closed preamble as <see cref="PeersController.Inbox"/>
    /// (root-routed here, alongside the rest of this controller, not under <c>/api/v1</c>).
    /// <para>
    /// The peer establishes <em>who</em>, and nothing else. Its tier assertion is not read: authority is
    /// per-host, so the vouched identity resolves against this node's own account store and is
    /// provisioned unapproved when it proves nothing here. An admin on one node is therefore not an
    /// admin on every node that trusts it, which is not what a peer relationship claims.
    /// </para>
    /// <para>
    /// Mints exactly like <see cref="Callback"/>: a fresh <c>sid</c>, both tokens minted carrying it,
    /// the refresh's <c>jti</c> stored as the session row's initial <c>CurrentJti</c>. Always JSON
    /// (<c>201</c>) — this is a node-to-node call, never the browser fragment handoff.
    /// </para>
    /// </summary>
    [AllowAnonymous]
    [HttpPost("/auth/cluster-session")]
    public async Task<IActionResult> ClusterSession([FromBody] ClusterSessionRequest? body, CancellationToken ct)
    {
        string? token = ExtractBearerToken(Request);
        if (token is null)
            return Error(StatusCodes.Status401Unauthorized, "invalid_cluster_token", "missing bearer token");

        ClusterPrincipal? principal = await clusterTokens.ValidateAsync(token).ConfigureAwait(false);
        if (principal is null)
            return Error(StatusCodes.Status401Unauthorized, "invalid_cluster_token",
                "invalid, expired, or unsigned cluster service token");

        if (!await peerGate.IsEnabledAsync(principal.NodeId).ConfigureAwait(false))
            return Error(StatusCodes.Status403Forbidden, "peer_disabled",
                $"node '{principal.NodeId}' is not an enabled peer of this cluster");

        if (body is null || string.IsNullOrWhiteSpace(body.DiscordId))
            return Error(StatusCodes.Status400BadRequest, "bad_request", "discordId is required");

        // The vouch wire names Discord explicitly (`discordId`) because that is the only provider a
        // peer can vouch for today; the identity is built provider-qualified so nothing downstream —
        // the token subject, the session row's key — has to know that.
        var identity = new KgsmIdentity(
            KgsmActorProvider.Discord, body.DiscordId, body.Username, body.DisplayName, null, []);

        // The peer authenticated this person and that is the whole of what it can tell us. Its tier
        // assertion (`body.Tier`) is deliberately not read: authority is per-host and comes from this
        // node's account store, so a vouched arrival resolves — or is provisioned unapproved — exactly
        // like one arriving through this node's own login. Otherwise an admin on one node would be an
        // admin on every node that trusts it, which is not what a peer relationship says.
        if (!users.Available)
            return Error(StatusCodes.Status502BadGateway, "authority_unavailable",
                users.UnavailableReason ?? "The KGSM account store is unavailable on this host.");

        LinkResult vouched;
        try
        {
            vouched = await users.Linking.ResolveOrProvisionAsync(
                identity, DateTimeOffset.UtcNow, options.PendingPolicy, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not resolve a vouched {Handle} against the KGSM account store.",
                identity.Handle);
            return Error(StatusCodes.Status502BadGateway, "authority_unavailable",
                "The KGSM account store could not be read.");
        }

        if (vouched.Outcome == LinkOutcome.PendingCapReached)
            return Error(StatusCodes.Status503ServiceUnavailable, "not_accepting_accounts",
                "This host is not accepting new accounts right now.");

        if (vouched.User!.Status == UserStatus.Disabled)
            return Error(StatusCodes.Status403Forbidden, "account_disabled",
                "That account is disabled on this host.");

        KgsmTier tier = vouched.User.EffectiveTier;

        // No userAgent: a vouch is a node-to-node call, and the calling node's HTTP client is not a
        // device anyone would recognise in their own session list.
        MintedSession minted = await MintSessionAsync(identity, tier, userAgent: null, ct);
        string sessionId = minted.SessionId;
        MintedToken access = minted.Access;
        MintedToken refresh = minted.Refresh;

        // API-internal, no kgsm event -> written directly (the auth.* posture, no double-write risk).
        // origin="api" (NOT the default "ui" — this is a node-to-node call, not a browser session).
        // The closed origin vocabulary has no per-node value, so the vouching peer's node id rides in
        // Meta instead — the forensic detail ("this session was minted on a vouch FROM node X"), never
        // fabricated into a fake origin.
        await RecordAuthAsync(AuditAction.AuthClusterSession, identity, tier,
            $"{identity.Display} joined via a cluster vouch from node '{principal.NodeId}'",
            sessionId, userAgent: null, ct, peerNode: principal.NodeId, origin: AuditOrigin.Api);

        return StatusCode(StatusCodes.Status201Created,
            new ClusterSessionResult(access.Token, refresh.Token, sessionId, access.ExpiresAt));
    }

    /// <summary>
    /// <c>POST /auth/cluster-session/request</c> — the user-authed vouch INITIATOR (<c>PLAN-peers.md</c>
    /// §7, gap G2): the browser calls this on the node the caller is already logged into (this node),
    /// never <see cref="ClusterSession"/> directly — the browser holds no cluster secret to authenticate
    /// with there. This node reads the caller's identity and tier off THEIR OWN validated session claims
    /// on THIS node — <b>never</b> from the request body, which carries only the target <c>nodeId</c> —
    /// so a caller can never assert a different identity/tier than the one this node already
    /// authenticated them as (forecloses privilege laundering). It then mints a fresh cluster service
    /// token, relays the caller's claims to the target peer's <see cref="ClusterSession"/> receiver, and
    /// hands back that peer's minted session verbatim — the SSO grant that carries a login on this node
    /// onto another with no re-authentication and no cluster secret ever reaching the browser.
    /// <list type="bullet">
    /// <item><c>201</c> — the peer's minted session (<see cref="ClusterSessionResult"/>), relayed as-is.</item>
    /// <item><c>400</c> <c>bad_request</c> — missing <c>nodeId</c>.</item>
    /// <item><c>401</c> <c>unauthorized</c> — no valid session on this node.</item>
    /// <item><c>404</c> <c>unknown_node</c> — <c>nodeId</c> isn't in this node's roster (a non-cluster
    /// node's roster is always empty, so a disabled/unconfigured cluster also lands here).</item>
    /// <item><c>403</c> <c>peer_disabled</c> — the target peer is disabled on this node.</item>
    /// <item><c>502</c> <c>peer_unreachable</c> — the peer was unreachable, refused the vouch, or
    /// returned an unparseable response.</item>
    /// </list>
    /// No audit row is written here — the RECEIVER (the target node) already audits
    /// <c>auth.cluster_session</c> for the minted session; this initiator is a pass-through relay.
    /// </summary>
    [Authorize(Policy = AuthPolicy.Viewer)]
    [HttpPost("/auth/cluster-session/request")]
    public async Task<IActionResult> RequestClusterSession([FromBody] ClusterVouchRequest? body, CancellationToken ct)
    {
        if (User.Identity is not ClaimsIdentity ci || SessionClaims.ReadIdentity(ci) is not { } caller)
            return Error(StatusCodes.Status401Unauthorized, "unauthorized", "no valid session");

        if (body is null || string.IsNullOrWhiteSpace(body.NodeId))
            return Error(StatusCodes.Status400BadRequest, "bad_request", "nodeId is required");

        // A disabled/unconfigured cluster has an empty roster anyway (PeersStore never seeds rows
        // without ClusterEnabled), so this is redundant with the lookup below — kept only so the
        // "no cluster here at all" case reads as an explicit early-out rather than a roster miss.
        if (!options.ClusterEnabled)
            return Error(StatusCodes.Status404NotFound, "unknown_node", $"node '{body.NodeId}' is not a known peer");

        PeerEntity? peer = await peers.GetByNodeIdAsync(body.NodeId, ct);
        if (peer is null)
            return Error(StatusCodes.Status404NotFound, "unknown_node", $"node '{body.NodeId}' is not a known peer");

        if (!peer.Enabled)
            return Error(StatusCodes.Status403Forbidden, "peer_disabled", $"node '{body.NodeId}' is disabled on this node");

        MintedClusterToken token = clusterTokens.Mint();

        // The node-to-node address (GossipUrl) when this peer has one, else its public Url — same
        // preference GossipWorker/OutboxDrainer use to reach a peer's own HTTP surface.
        string url = $"{(peer.GossipUrl ?? peer.Url).TrimEnd('/')}/auth/cluster-session";

        // Built from the CALLER'S CLAIMS, never body — body carries only nodeId (see the XML doc above).
        var reqBody = new ClusterSessionRequest(
            caller.Subject, caller.Username, caller.Display, KgsmTiers.ToWire(SessionClaims.ReadTier(ci)));

        HttpClient http = httpClientFactory.CreateClient(OutboxDrainer.HttpClientName);
        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            request.Content = JsonContent.Create(reqBody, options: VouchJsonOptions);
            response = await http.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "cluster-session vouch to node '{NodeId}' failed", body.NodeId);
            return Error(StatusCodes.Status502BadGateway, "peer_unreachable", $"node '{body.NodeId}' is unreachable");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("cluster-session vouch to node '{NodeId}' rejected: HTTP {Status}",
                    body.NodeId, (int)response.StatusCode);
                return Error(StatusCodes.Status502BadGateway, "peer_unreachable",
                    $"node '{body.NodeId}' refused the vouch");
            }

            ClusterSessionResult? result;
            try
            {
                result = await response.Content.ReadFromJsonAsync<ClusterSessionResult>(VouchJsonOptions, ct);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "cluster-session vouch to node '{NodeId}' returned an unparseable body",
                    body.NodeId);
                result = null;
            }
            if (result is null)
                return Error(StatusCodes.Status502BadGateway, "peer_unreachable",
                    $"node '{body.NodeId}' returned an unparseable vouch response");

            return StatusCode(StatusCodes.Status201Created, result);
        }
    }

    /// <summary>302 the SPA with the OAuth outcome in the URL fragment. The target is the single
    /// configured <see cref="ApiOptions.AuthFrontendUrl"/> — never a request-supplied URL, so there is
    /// no open-redirect surface.</summary>
    private IActionResult FrontendRedirect(string fragment) =>
        Redirect($"{options.AuthFrontendUrl}#{fragment}");

    /// <summary>A failed/denied/unconfigured outcome: when the SPA handoff is on, redirect with
    /// <c>#error=&lt;code&gt;</c>; otherwise the unchanged JSON error envelope.</summary>
    private IActionResult Fail(int status, string code, string message) =>
        options.FrontendRedirectEnabled
            ? FrontendRedirect(Frag(("error", code)))
            : Error(status, code, message);

    /// <summary>Build a URL fragment <c>k=v&amp;k=v</c>, percent-encoding values and dropping empties.</summary>
    private static string Frag(params (string Key, string? Value)[] parts)
    {
        var sb = new System.Text.StringBuilder();
        foreach ((string key, string? value) in parts)
        {
            if (string.IsNullOrEmpty(value)) continue;
            if (sb.Length > 0) sb.Append('&');
            sb.Append(key).Append('=').Append(Uri.EscapeDataString(value!));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Rotate the access AND refresh tokens from a still-valid refresh token (presented as the
    /// <c>Authorization: Bearer</c>), no Discord round-trip. M4·c with rotation: the session row's
    /// <c>CurrentJti</c> is validated against the presented refresh's <c>jti</c> (reuse detection — a
    /// stale jti = an OLD/STOLEN refresh token → <c>401</c>); on a match, BOTH tokens are re-minted
    /// (the refresh's <c>jti</c> rotates, the row slides its <c>Expires</c> forward — the rolling 30-day
    /// window the user directive opened — and bumps <c>LastSeen</c>). The SPA MUST adopt the new
    /// refresh token from the response body on every call (the old one is dead). Role is NOT re-checked
    /// here (the long-term "transparent role changes" idea, deferred).
    /// </summary>
    [AllowAnonymous]
    [HttpPost("/auth/session/refresh")]
    public async Task<IActionResult> Refresh()
    {
        string? token = BearerToken();
        if (token is null)
            return Error(StatusCodes.Status401Unauthorized, "unauthorized", "missing refresh token");

        RefreshClaims? claims = await tokens.ReadRefreshAsync(token);
        if (claims is null)
            return Error(StatusCodes.Status401Unauthorized, "unauthorized",
                "the refresh token is invalid or expired");

        // M4·c rotation — mint BOTH tokens upfront. Each gets a fresh jti; the access jti is
        // informational only (the per-request validator doesn't check jti), the refresh jti is the
        // reuse-detection key that becomes the row's stored CurrentJti. We mint before the row-update
        // so the row-update can store the new refresh's jti in a single serialized round-trip. Both
        // carry the SAME sid (D7/D8: same sid across a session's lifetime). The new refresh
        // SURFACES from the response to the SPA.
        DateTimeOffset now = DateTimeOffset.UtcNow;
        MintedToken access = tokens.MintAccess(claims.Identity, claims.Tier, claims.SessionId);
        MintedToken refresh = tokens.MintRefresh(claims.Identity, claims.Tier, claims.SessionId);

        // Validate the presented refresh's jti against the row's stored CurrentJti + slide Expires
        // + bump LastSeen + store the new refresh's jti (rotation). Returns false when: no row / row
        // revoked / jti mismatch (stale/old/stolen refresh — reuse detection). The 401 here is the
        // SPA's signal to re-authenticate via Discord OAuth; an attacker with an old refresh token
        // keeps getting 401s while the legit user's NEW token (from their previous refresh) succeeds.
        // ⚠ No grace period — a tab race resolves to ONE tab 401ing (re-auth on reload); acceptable
        // for a small panel (the alternative — grace-period jti tracking — needs another column).
        DateTimeOffset newExpires = now + TimeSpan.FromDays(options.SessionsRefreshAbsoluteDays);
        bool rotated = await sessions.UpdateForRefreshAsync(
            claims.SessionId, claims.Jti, refresh.Jti, newExpires, CancellationToken.None);
        if (!rotated)
            return Error(StatusCodes.Status401Unauthorized, "unauthorized",
                "the refresh token is invalid, expired, or has been superseded");

        return Ok(new RefreshResponse(access.Token, refresh.Token, KgsmTiers.ToWire(claims.Tier), access.ExpiresAt));
    }

    /// <summary>
    /// The profile snapshot behind the bearer (captured at login), or <c>401</c>. §3·f divergence:
    /// this is the login-time snapshot, NOT a fresh live fetch — we keep no Discord token to re-fetch with.
    /// </summary>
    [Authorize]
    [HttpGet("/auth/session")]
    public ActionResult<SessionResponse> Session()
    {
        KgsmIdentity? id = User.Identity is System.Security.Claims.ClaimsIdentity ci
            ? SessionClaims.ReadIdentity(ci)
            : null;
        if (id is null)
            return Error(StatusCodes.Status401Unauthorized, "unauthorized", "no session");

        return new SessionResponse(
            new SessionUser(id.Handle, id.Username, id.Display, id.AvatarUrl),
            id.Scopes);
    }

    /// <summary>
    /// End the session — server-side (M4·c). Reads the <c>sid</c> off the bearer, revokes the session
    /// row (<c>Revoked=true</c>) and evicts it from the validator cache, so every token carrying that
    /// sid (the access bearer AND the refresh token) stops authorizing on its next request (~instant via
    /// the cache eviction; the ≤15min access TTL is the hard ceiling regardless). Always <c>204</c>
    /// (idempotent — an already-revoked or absent session is a no-op). If the call carries a resolvable
    /// bearer we also record an <c>auth.logout</c> audit row (best-effort); an unauthenticated logout
    /// (no bearer) can't be attributed, so it simply returns 204 with no revoke and no row.
    /// </summary>
    [AllowAnonymous]
    [HttpPost("/auth/logout")]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        if (User.Identity is ClaimsIdentity ci && ci.IsAuthenticated
            && SessionClaims.ReadIdentity(ci) is { } id)
        {
            // M4·c — revoke the session server-side. Read the sid off the bearer, flip the row to
            // Revoked=true (idempotent) and evict the validator cache so the next request on this sid
            // 401s immediately rather than waiting for the 5s TTL backstop. Best-effort: a failed
            // revoke must never turn a logout into an error (the client is dropping its tokens anyway,
            // and the access TTL still bounds the window) — log it, then still write the audit row.
            string? sid = SessionClaims.ReadSessionId(ci);
            if (!string.IsNullOrEmpty(sid))
            {
                try
                {
                    await sessions.RevokeAsync(sid, ct);
                    sessionValidator.Evict(sid);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "session revoke on logout failed (non-fatal) sid={Sid}", sid);
                }
            }
            // The audit Meta carries the sid (forensics: links logout → the revoked session row). No
            // userAgent here — recentLogins reads `auth.login` rows only, and logout's own UA isn't
            // otherwise useful; keep the meta minimal for the action it's on.
            await RecordAuthAsync(AuditAction.AuthLogout, id, SessionClaims.ReadTier(ci),
                $"{id.Display} logged out", sid, null, ct);
        }
        return NoContent();
    }

    /// <summary>A freshly minted session: the id both tokens carry, and the tokens themselves.</summary>
    private readonly record struct MintedSession(string SessionId, MintedToken Access, MintedToken Refresh);

    /// <summary>
    /// Mint a session for a verified identity and persist its registry row. Every door into this host
    /// — the OAuth callback, a password, a peer's vouch — goes through here, so a session means the
    /// same thing whichever one it came through.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both tokens carry the same <c>sid</c> for the session's whole life; each carries its own
    /// <c>jti</c>, and the refresh's becomes the row's <c>CurrentJti</c> — the key reuse detection
    /// compares against on the refresh path. The row's <c>Expires</c> starts at
    /// <c>now + SessionsRefreshAbsoluteDays</c> and slides forward on each successful refresh.
    /// </para>
    /// <para>
    /// The row write is best-effort and must never fail a login — but it is logged loudly, because the
    /// per-request validator rejects a token whose <c>sid</c> has no row. The honest recovery is a
    /// forced relogin within the access token's ~15-minute ceiling; a silent failure would look like
    /// a login that worked and then stopped.
    /// </para>
    /// </remarks>
    private async Task<MintedSession> MintSessionAsync(
        KgsmIdentity identity, KgsmTier tier, string? userAgent, CancellationToken ct)
    {
        string sessionId = "sid_" + Guid.NewGuid().ToString("N");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        MintedToken access = tokens.MintAccess(identity, tier, sessionId);
        MintedToken refresh = tokens.MintRefresh(identity, tier, sessionId);

        try
        {
            await sessions.CreateAsync(
                sessionId, identity.Handle, options.HostId,
                created: now,
                expires: now + TimeSpan.FromDays(options.SessionsRefreshAbsoluteDays),
                userAgent, refresh.Jti, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Session row insert failed (non-fatal to the sign-in, but the validator will reject sid={Sid} — forced relogin follows)",
                sessionId);
        }

        return new MintedSession(sessionId, access, refresh);
    }

    /// <summary>
    /// Write a <c>user.*</c> row for an account this login created, with the arriving identity as the
    /// actor — nobody else was involved, and naming the API as the actor would hide who arrived.
    /// </summary>
    private async Task RecordUserAsync(
        string action, KgsmIdentity identity, KgsmUser account, string summary, CancellationToken ct)
    {
        try
        {
            await audit.AppendAsync(new AuditWrite(
                Ts: DateTimeOffset.UtcNow,
                Origin: AuditOrigin.Ui,
                Actor: new AuditActor(ActorKind.User, identity.Username, identity.Provider),
                Action: action,
                Severity: AuditSeverity.Warn,
                Target: new AuditTarget("user", account.UserId, account.Username),
                ServerId: null,
                HostId: options.HostId,
                Summary: summary,
                Meta: new Dictionary<string, string>
                {
                    ["userId"] = account.UserId,
                    ["identity"] = identity.Handle,
                    ["status"] = UserStatuses.ToWire(account.Status),
                    ["tier"] = KgsmTiers.ToWire(account.Tier),
                }),
                ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "audit {Action} write failed (non-fatal)", action);
        }
    }

    /// <summary>The calling device, for a human reading their own session list. Never authority.</summary>
    private string? UserAgent()
    {
        string? userAgent = Request.Headers.UserAgent.ToString();
        return string.IsNullOrWhiteSpace(userAgent) ? null : userAgent;
    }

    // The bearer from the Authorization header, or null. Used by /refresh (which can't use [Authorize]:
    // the refresh token is deliberately rejected as an access bearer by the JwtBearer pipeline).
    private string? BearerToken()
    {
        string? header = Request.Headers.Authorization;
        if (string.IsNullOrEmpty(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return null;
        string token = header["Bearer ".Length..].Trim();
        return token.Length == 0 ? null : token;
    }

    /// <summary>Extract a bearer token from the <c>Authorization</c> header, or <see langword="null"/>
    /// if absent/not a bearer. Used by <see cref="ClusterSession"/> — a node-to-node call authenticates
    /// via the cluster service token, not the Discord-derived user session (<see cref="BearerToken"/>
    /// above), and has no other credential form. Mirrors <c>PeersController.ExtractBearerToken</c> exactly.</summary>
    private static string? ExtractBearerToken(HttpRequest request)
    {
        string? header = request.Headers[HeaderNames.Authorization];
        const string prefix = "Bearer ";
        return header is not null && header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? header[prefix.Length..].Trim()
            : null;
    }

    private ObjectResult Error(int statusCode, string code, string message) =>
        StatusCode(statusCode, new ErrorEnvelope(new ErrorBody(code, message)));

    // Write an API-internal auth.* audit row. origin defaults to "ui": an interactive Discord OAuth
    // login/logout is a human acting through the web surface (there is no headless login path); the
    // cluster SSO vouch (ClusterSession) is the one caller that overrides it to "api" (origin="ui"
    // would misattribute a node-to-node call as a browser session). actor = the Discord identity
    // (kind=user, provider=discord) either way — a vouched session still belongs to that Discord user,
    // just asserted by a peer instead of a fresh OAuth round-trip. Best-effort: a failed audit write is
    // logged, never fatal. M4·c — the `sid` lands in Meta so a login/logout event links to its session
    // row (forensics). M4·c Increment 7 — `userAgent` lands in Meta (additive to the existing
    // direct-write, not a new action or a second writer) so `/me`'s recent-logins read can honestly
    // report "which device"; omitted (not empty-string) when blank, matching the never-fabricate
    // posture elsewhere in Meta. `peerNode` (the cluster SSO vouch) is the analogous additive: the
    // vouching peer's node id, when present, so a vouched login's audit row still names which node
    // asserted the identity.
    private async Task RecordAuthAsync(string action, KgsmIdentity id, KgsmTier tier, string summary,
        string? sid, string? userAgent, CancellationToken ct, string? peerNode = null, string origin = AuditOrigin.Ui)
    {
        try
        {
            var meta = new Dictionary<string, string> { ["tier"] = KgsmTiers.ToWire(tier) };
            if (!string.IsNullOrEmpty(sid))
                meta["sid"] = sid;
            if (!string.IsNullOrEmpty(userAgent))
                meta["userAgent"] = userAgent;
            if (!string.IsNullOrEmpty(peerNode))
                meta["peerNode"] = peerNode;
            await audit.AppendAsync(new AuditWrite(
                Ts: DateTimeOffset.UtcNow,
                Origin: origin,
                Actor: new AuditActor(ActorKind.User, id.Username, id.Provider),
                Action: action,
                Severity: AuditSeverity.Info,
                Target: null,
                ServerId: null,
                HostId: options.HostId,
                Summary: summary,
                Meta: meta),
                ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "audit {Action} write failed (non-fatal)", action);
        }
    }

    // The CSRF state cookie's attributes — shared by the set (at /start) and the delete (at /callback,
    // where Path must match for the deletion to take). Secure tracks the scheme so it works on an http
    // loopback dev host yet is Secure on a real https host; SameSite=Lax (NOT Strict) so the cookie
    // still rides Discord's top-level cross-site redirect back to the callback.
    private CookieOptions StateCookieOptions() => new()
    {
        HttpOnly = true,
        Secure = Request.IsHttps,
        SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax,
        Path = "/auth/discord",
        IsEssential = true,
        MaxAge = StateTtl,
    };
}
