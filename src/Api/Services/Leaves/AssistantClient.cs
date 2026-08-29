using System.Net.Http.Json;
using System.Net.Sockets;

using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Kgsm.Assistant.Relay;
using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Discord;

namespace TheKrystalShip.Api.Services.Leaves;

/// <summary>
/// Builds the shared <see cref="RelayPrincipal"/> from this API's own notion of a signed-in caller.
/// </summary>
/// <remarks>
/// The principal type itself belongs to the relay package, so this API and kgsm-bot forward identity
/// and authority in one shape. This factory does not, because it speaks Discord identities — which is
/// how <em>this</em> surface authenticates, not something every relaying leaf has.
/// </remarks>
internal static class RelayPrincipals
{
    /// <summary>The caller as forwarded on their own behalf.</summary>
    public static RelayPrincipal From(KgsmIdentity identity, KgsmTier tier) =>
        new(identity.Subject, identity.Display, tier);
}

/// <summary>
/// The kgsm-assistant leaf client: a typed <see cref="HttpClient"/> onto the assistant's HTTP
/// surface (a co-located leaf reached over plain TCP, unlike the monitor's unix socket). Today
/// it exposes only a liveness <see cref="ProbeAsync"/> used to report the architecture §4·b
/// assistant capability, but it is the deliberate home for the assistant's real surface as it
/// lands — the tool catalog, capability discovery, and the SSE turn relay (M7) — so callers
/// depend on typed methods here rather than raw HTTP scattered across the aggregator.
/// </summary>
/// <remarks>
/// When the assistant is not provisioned (no base URL configured) the client is constructed in a
/// disabled state (<see cref="IsProvisioned"/> false) and every call short-circuits — the §4·b
/// capability renders <c>absent</c>, never a broken <c>down</c>. Registered as a singleton; the
/// recycling connection pool (<see cref="SocketsHttpHandler.PooledConnectionLifetime"/>) is the
/// documented way to keep a process-lifetime <see cref="HttpClient"/> from pinning a stale
/// connection without IHttpClientFactory. Note the client's <see cref="HttpClient.Timeout"/> is
/// left at its default on purpose: the 2s budget is the <em>liveness probe's</em>, applied per
/// call via a linked token — it must not become a class-wide ceiling on the slower calls (tool
/// fetch, SSE connect) this client will grow.
/// </remarks>
public sealed class AssistantClient : HttpClient
{
    // Liveness-probe budget only — bound the probe so a hung assistant can never stall a /hosts
    // request. Applied per call (NOT as HttpClient.Timeout) so future, slower calls are free to
    // set their own budget. Aligned with the other leaf probes (HostAggregator.ProbeTimeout).
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    // Per-call budget for the short request/response relays (conversation reads, delete, compact) —
    // preserves the pre-infinite-Timeout ceiling now that the class Timeout is unbounded (below).
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(120);

    private readonly ILogger<AssistantClient> _logger;
    private readonly LeafRegistry _registry;
    // Identity and authority go on the wire through the assistant's own relay contract, so this API
    // cannot drift from the filter that reads them — or from the bot that writes them too.
    private readonly AssistantRelay _relay;
    private readonly bool _hasBaseUrl;

    public AssistantClient(ApiOptions options, LeafRegistry registry, ILogger<AssistantClient> logger)
        : base(NewHandler(), disposeHandler: true)
    {
        _logger = logger;
        _registry = registry;
        _relay = new AssistantRelay(options.AssistantRelaySecret, RelayLeaf.Api);

        // Unbounded class Timeout — the short relays set their OWN budget via a linked token (probe 2s,
        // reads 120s), while the SSE turn and the streamed blueprint finalize are body-length-unbounded by
        // design (ResponseHeadersRead + heartbeats). The 100s default would otherwise cap a minutes-long
        // finalize and any future slow call — exactly the class-wide ceiling the type remarks warn against.
        Timeout = System.Threading.Timeout.InfiniteTimeSpan;

        // Set the base address from the configured URL whenever one is present (independent of the runtime
        // provisioning flag) so a connect/disconnect arms/disarms the client live without a restart. Without
        // a configured URL there is no endpoint to flip to (no universal default), so the runtime flip can
        // only ever report the capability down — the honest limit, noted in the feature plan.
        if (Uri.TryCreate(options.AssistantBaseUrl, UriKind.Absolute, out Uri? baseUri))
        {
            BaseAddress = baseUri;
            _hasBaseUrl = true;
        }

        // An assistant to reach and no secret to reach it with is a relay the leaf will refuse, and the
        // refusal surfaces one turn later as a 502 with nothing pointing at the cause. The secret is
        // normally the host's own, minted on first look, so getting here means the file could be neither
        // read nor created — a provisioning fault, and one that is invisible until somebody asks a
        // question. Say it once, at startup, naming the file.
        if (_hasBaseUrl && !_relay.IsConfigured)
            _logger.LogWarning(
                "no relay secret — the assistant is configured at {BaseUrl} but will refuse every turn this "
                + "API forwards. The host's own secret is {Path}, which could not be read or created; check "
                + "that directory is owned by the account this service runs as",
                options.AssistantBaseUrl, options.RelaySecretPath);
    }

    /// <summary>True when the assistant is provisioned (connected) on this host at runtime AND a base URL is
    /// configured to reach it. The capability/relay calls all gate on this, so disconnecting the assistant
    /// disarms them live.</summary>
    public bool IsProvisioned => _hasBaseUrl && _registry.IsProvisioned(ProvisionableLeaf.Assistant);

    /// <summary>
    /// Opens the assistant's <c>POST /turn</c> as an SSE stream on a verified end-user's behalf (M7):
    /// posts <paramref name="turnBody"/> with <c>Accept: text/event-stream</c> and the trusted-relay
    /// headers — the shared <c>X-Relay-Secret</c>, the forwarded Discord identity (<c>X-Relay-User</c>
    /// / <c>X-Relay-User-Name</c>), the caller's verified tier (<c>X-Relay-Tier</c>, which is the
    /// authority to PROPOSE a command), the per-turn <c>X-Relay-Auto-Act</c> (from
    /// <paramref name="autoAct"/>, the admin-only authority to AUTO-RUN lifecycle commands without
    /// confirmation), and the optional per-chat <c>X-Relay-Conversation-Id</c>
    /// (from <paramref name="conversationId"/>) that sub-scopes the user's assistant memory so each SPA
    /// chat is a fresh context window — and returns the upstream response
    /// with <em>headers read only</em>, so
    /// the caller can relay the body frames verbatim. The caller <strong>owns disposal</strong>: disposing
    /// the response aborts the upstream request, which makes the assistant abort generation. Returns
    /// <see langword="null"/> when the assistant isn't provisioned on this host.
    /// </summary>
    /// <remarks>
    /// <c>ResponseHeadersRead</c> is deliberate: <see cref="HttpClient.Timeout"/> then bounds only the
    /// connect+headers phase, never the long-lived SSE body — exactly why this client's timeout is left at
    /// its default (see the type remarks). Caller cancellation (the client disconnecting) flows through
    /// <paramref name="ct"/> and tears the whole chain down.
    /// </remarks>
    public async Task<HttpResponseMessage?> OpenTurnStreamAsync(
        object turnBody, RelayPrincipal caller, bool autoAct,
        string? conversationId, CancellationToken ct)
    {
        if (!IsProvisioned)
            return null;

        var request = new HttpRequestMessage(HttpMethod.Post, "/turn")
        {
            Content = JsonContent.Create(turnBody),
        };
        request.Headers.Accept.ParseAdd("text/event-stream");
        // The identity and its verified tier, plus this turn's auto-accept decision and per-chat
        // sub-scope — the per-call parts ride the same writer so they cannot be forwarded apart.
        _relay.Write(request, caller, new RelayCall(autoAct, conversationId));
        return await SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Lists the verified end-user's own past conversations (the reverse path): <c>GET /conversations</c>
    /// on their behalf, forwarding the trusted-relay identity (<c>X-Relay-Secret</c> + <c>X-Relay-User</c>
    /// / <c>X-Relay-User-Name</c>) so the assistant scopes the listing to <c>web:{userId}</c>. The body is
    /// the assistant's JSON verbatim (an array of conversation summaries) — the caller relays it. Returns
    /// <see langword="null"/> when the assistant isn't provisioned; the caller <strong>owns disposal</strong>.
    /// </summary>
    public Task<HttpResponseMessage?> GetConversationsAsync(
        RelayPrincipal caller, CancellationToken ct) =>
        RelaySendAsync(HttpMethod.Get, "/conversations", caller, ct);

    /// <summary>
    /// Loads one of the verified end-user's conversations: <c>GET /conversations/{chatId}</c> on their
    /// behalf with the trusted-relay identity. The assistant composes the full key
    /// (<c>web:{userId}:{chatId}</c>) server-side, so <paramref name="chatId"/> can only ever address the
    /// caller's OWN conversation. The body is the assistant's transcript JSON verbatim. Returns
    /// <see langword="null"/> when the assistant isn't provisioned; the caller <strong>owns disposal</strong>.
    /// </summary>
    public Task<HttpResponseMessage?> GetConversationAsync(
        RelayPrincipal caller, string chatId, CancellationToken ct) =>
        RelaySendAsync(HttpMethod.Get, $"/conversations/{Uri.EscapeDataString(chatId)}", caller, ct);

    /// <summary>
    /// Soft-deletes one of the verified end-user's conversations: <c>DELETE /conversations/{chatId}</c> on
    /// their behalf with the trusted-relay identity. The assistant composes the full key
    /// (<c>web:{userId}:{chatId}</c>) server-side, so <paramref name="chatId"/> can only ever address the
    /// caller's OWN conversation; it appends a tombstone — the transcript (the corpus) is retained, only the
    /// listing hides it. The upstream answers <c>204</c>. Returns <see langword="null"/> when the assistant
    /// isn't provisioned; the caller <strong>owns disposal</strong>.
    /// </summary>
    public Task<HttpResponseMessage?> DeleteConversationAsync(
        RelayPrincipal caller, string chatId, CancellationToken ct) =>
        RelaySendAsync(HttpMethod.Delete, $"/conversations/{Uri.EscapeDataString(chatId)}", caller, ct);

    /// <summary>
    /// Compacts one of the verified end-user's conversations: <c>POST /conversations/{chatId}/compact</c> on
    /// their behalf with the trusted-relay identity. The assistant composes the full key
    /// (<c>web:{userId}:{chatId}</c>) server-side, so <paramref name="chatId"/> can only ever address the
    /// caller's OWN conversation; it summarises the history in place (non-destructive — a checkpoint is
    /// appended, the transcript is retained) and answers <c>200</c> with a CompactionOutcome JSON relayed
    /// verbatim. Returns <see langword="null"/> when the assistant isn't provisioned; the caller
    /// <strong>owns disposal</strong>.
    /// </summary>
    public Task<HttpResponseMessage?> CompactConversationAsync(
        RelayPrincipal caller, string chatId, CancellationToken ct) =>
        RelaySendAsync(HttpMethod.Post, $"/conversations/{Uri.EscapeDataString(chatId)}/compact", caller, ct);

    /// <summary>
    /// Records how the verified end-user judged one of their own answers:
    /// <c>POST /conversations/{chatId}/turns/{turnId}/feedback</c> on their behalf with the trusted-relay
    /// identity. Deliberately NOT an admin call — this is satisfaction, written by the person the answer
    /// was for, so it carries the ordinary forwarded identity and the assistant scopes it to that user's
    /// own conversation. The assistant additionally verifies the turn belongs to that conversation, so a
    /// tampered <paramref name="turnId"/> cannot reach a stranger's turn; it answers <c>204</c>, or
    /// <c>404</c> when the turn is not the caller's. Returns <see langword="null"/> when the assistant
    /// isn't provisioned; the caller <strong>owns disposal</strong>.
    /// </summary>
    public Task<HttpResponseMessage?> SetTurnFeedbackAsync(
        RelayPrincipal caller, string chatId, long turnId,
        string? rating, string? note, CancellationToken ct) =>
        RelaySendAsync(
            HttpMethod.Post,
            $"/conversations/{Uri.EscapeDataString(chatId)}/turns/{turnId}/feedback",
            caller, ct,
            body: JsonContent.Create(new { rating, note }));

    /// <summary>
    /// Lists everyone who has talked to this host's assistant: <c>GET /admin/conversations/users</c>, the
    /// index an administrator picks from when reviewing how the assistant is answering. Carries
    /// the caller's verified tier — the assistant's review surface is fail-closed on it, and this action is
    /// admin-gated, so anyone reaching here forwards an admin tier. The body is the assistant's JSON verbatim.
    /// Returns <see langword="null"/> when the assistant isn't provisioned; the caller <strong>owns
    /// disposal</strong>.
    /// </summary>
    public Task<HttpResponseMessage?> GetReviewUsersAsync(
        RelayPrincipal caller, CancellationToken ct) =>
        RelaySendAsync(HttpMethod.Get, "/admin/conversations/users", caller, ct);

    /// <summary>
    /// Reads the corpus roll-up behind the assistant's operator overview:
    /// <c>GET /admin/conversations/stats</c> — outcome mix, answer-time distribution, per-tool behaviour,
    /// prompt-version buckets and daily volume, plus the leaf's live runtime. Admin-gated for the same
    /// reason the transcripts are: it describes other people's conversations, in aggregate. The body is
    /// the assistant's JSON verbatim. Returns <see langword="null"/> when the assistant isn't provisioned;
    /// the caller <strong>owns disposal</strong>.
    /// </summary>
    public Task<HttpResponseMessage?> GetReviewStatsAsync(
        RelayPrincipal caller, CancellationToken ct) =>
        RelaySendAsync(HttpMethod.Get, "/admin/conversations/stats", caller, ct);

    /// <summary>
    /// Lists one user's conversations for review: <c>GET /admin/conversations?user={userId}</c>. Unlike the
    /// self-service listing, <paramref name="ofUserId"/> names SOMEONE ELSE — which is exactly why the call
    /// needs an admin tier forwarded and why its controller is admin-gated. Soft-deleted conversations are
    /// included, flagged, by the assistant. Returns <see langword="null"/> when the assistant isn't
    /// provisioned; the caller <strong>owns disposal</strong>.
    /// </summary>
    public Task<HttpResponseMessage?> GetReviewConversationsAsync(
        RelayPrincipal caller, string ofUserId, CancellationToken ct) =>
        RelaySendAsync(HttpMethod.Get, $"/admin/conversations?user={Uri.EscapeDataString(ofUserId)}",
            caller, ct);

    /// <summary>
    /// Reads one conversation's transcript for review: <c>GET /admin/conversations/{handle}</c>.
    /// <paramref name="handle"/> is the OPAQUE id the assistant's listing minted — the API neither composes
    /// nor interprets it, it forwards what the client was offered. Returns <see langword="null"/> when the
    /// assistant isn't provisioned; the caller <strong>owns disposal</strong>.
    /// </summary>
    public Task<HttpResponseMessage?> GetReviewConversationAsync(
        RelayPrincipal caller, string handle, CancellationToken ct) =>
        RelaySendAsync(HttpMethod.Get, $"/admin/conversations/{Uri.EscapeDataString(handle)}",
            caller, ct);

    /// <summary>
    /// Finalizes a staged confirmation on the verified end-user's behalf (the blueprint-review Save, and
    /// any future confirm): <c>POST /confirm</c> with the trusted-relay identity + the forwarded body
    /// (<c>{ token, editedContent }</c>). The assistant validates the single-use token, re-derives action
    /// authority from the forwarded <c>X-Relay-Tier</c> (this endpoint is operator-gated, so any caller
    /// reaching it forwards operator or better), and runs the whole finalize pipeline.
    /// <para>
    /// A blueprint finalize is <em>minutes</em> of test-install → verify → repair, so — like the turn — the
    /// caller asks for it <c>text/event-stream</c> and the assistant STREAMS it: <c>progress</c> steps +
    /// keep-alive heartbeats keep the socket warm through the long silent stretches, then a terminal
    /// <c>result</c> frame carries the <c>ConfirmResponse</c>. <c>ResponseHeadersRead</c> (as the turn does)
    /// hands the stream back the instant the headers land, so the long body isn't bound by
    /// <see cref="HttpClient.Timeout"/>; the finalize is internally bounded by the engine's own command
    /// timeouts, and a client disconnect tears the chain down. Returns <see langword="null"/> when the
    /// assistant isn't provisioned; the caller <strong>owns disposal</strong> (and streams the body).
    /// </para>
    /// </summary>
    public async Task<HttpResponseMessage?> ConfirmAsync(
        RelayPrincipal caller, AssistantConfirmRequest body, CancellationToken ct)
    {
        if (!IsProvisioned)
            return null;

        // Forward the exact field names the assistant's ConfirmRequest expects (lowercase-first), same
        // explicit style as the turn relay — never rely on the upstream's case-insensitive binding.
        var request = new HttpRequestMessage(HttpMethod.Post, "/confirm")
        {
            Content = JsonContent.Create(new { token = body.Token, editedContent = body.EditedContent }),
        };
        // Opt into the streamed finalize (progress + heartbeats + terminal `result`). The assistant falls
        // back to a buffered ConfirmResponse for any caller that doesn't ask for the stream.
        request.Headers.Accept.ParseAdd("text/event-stream");
        // The confirm EXECUTES a mutation (blueprint finalize install/verify), so the assistant re-derives
        // action authority the same way it did when it PROPOSED — from the forwarded tier. A relay host
        // with no Discord config of its own has no other authority source, so without this the finalize
        // is denied.
        _relay.Write(request, caller);

        // ResponseHeadersRead: return the stream as soon as the headers land so the minutes-long finalize
        // body isn't HttpClient.Timeout-bound (the class Timeout is Infinite; see the ctor). The heartbeats
        // keep the socket alive, so no separate read-timeout is needed.
        return await SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Lists what the assistant has written down about the verified end-user: <c>GET /memories</c> on
    /// their behalf with the trusted-relay identity. The assistant scopes the listing to the caller's
    /// own memory owner key, so this can only ever list the caller's OWN memories. The body is the
    /// assistant's JSON verbatim (an array of <c>MemoryDto</c>). Returns <see langword="null"/> when the
    /// assistant isn't provisioned; the caller <strong>owns disposal</strong>.
    /// </summary>
    public Task<HttpResponseMessage?> GetMemoriesAsync(
        RelayPrincipal caller, CancellationToken ct) =>
        RelaySendAsync(HttpMethod.Get, "/memories", caller, ct);

    /// <summary>
    /// What a memory may weigh on this host: <c>GET /memories/limits</c>. It describes the assistant,
    /// not the caller, and is carried on the same forwarded identity as the reads beside it so an
    /// editor gets its counters from the leaf that will enforce them. Returns <see langword="null"/>
    /// when the assistant isn't provisioned; the caller <strong>owns disposal</strong>.
    /// </summary>
    public Task<HttpResponseMessage?> GetMemoryLimitsAsync(
        RelayPrincipal caller, CancellationToken ct) =>
        RelaySendAsync(HttpMethod.Get, "/memories/limits", caller, ct);

    /// <summary>
    /// Writes one memory by hand on the verified end-user's behalf: <c>PUT /memories/{key}</c> with
    /// the trusted-relay identity. Create and correct are the same call upstream, because a memory is
    /// revised by rewriting its key. The assistant scopes <paramref name="key"/> under the caller's own
    /// memory owner key, so it can only ever address the caller's OWN memory, and it refuses a length
    /// or the per-owner cap itself — the refusal is relayed verbatim rather than restated here, since
    /// the leaf holds the numbers. Returns <see langword="null"/> when the assistant isn't provisioned;
    /// the caller <strong>owns disposal</strong>.
    /// </summary>
    public Task<HttpResponseMessage?> WriteMemoryAsync(
        RelayPrincipal caller, string key, string? summary, string? body, CancellationToken ct) =>
        RelaySendAsync(
            HttpMethod.Put, $"/memories/{Uri.EscapeDataString(key)}", caller, ct,
            body: JsonContent.Create(new { summary, body }));

    /// <summary>
    /// Forgets one thing the assistant has written down about the verified end-user:
    /// <c>DELETE /memories/{key}</c> on their behalf with the trusted-relay identity. The assistant
    /// scopes <paramref name="key"/> under the caller's own memory owner key, so it can only ever
    /// address the caller's OWN memory; the upstream delete is idempotent. Returns
    /// <see langword="null"/> when the assistant isn't provisioned; the caller <strong>owns
    /// disposal</strong>.
    /// </summary>
    public Task<HttpResponseMessage?> DeleteMemoryAsync(
        RelayPrincipal caller, string key, CancellationToken ct) =>
        RelaySendAsync(HttpMethod.Delete, $"/memories/{Uri.EscapeDataString(key)}", caller, ct);

    // Shared relay-on-the-user's-behalf (GET read / DELETE soft-delete / compact / review): forwards the
    // secret and the caller, reads the small body fully. Self-bounded to ReadTimeout via a linked token —
    // these are short request/response calls, not the long SSE stream or the minutes-long confirm (the
    // class Timeout is unbounded, so each call budgets itself).
    private async Task<HttpResponseMessage?> RelaySendAsync(
        HttpMethod method, string path, RelayPrincipal caller, CancellationToken ct,
        HttpContent? body = null)
    {
        if (!IsProvisioned)
            return null;

        var request = new HttpRequestMessage(method, path) { Content = body };
        request.Headers.Accept.ParseAdd("application/json");
        _relay.Write(request, caller);

        using var timed = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timed.CancelAfter(ReadTimeout);
        return await SendAsync(request, timed.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Liveness probe for the §4·b assistant capability, run through the assistant's own shared probe
    /// so every leaf that consumes it agrees on what "up" means: <c>GET /health</c>, a 2xx, within
    /// <see cref="ProbeTimeout"/>. Never throws. An unprovisioned assistant is short-circuited here
    /// rather than probed, so the capability renders <c>absent</c> instead of a broken <c>down</c>.
    /// </summary>
    public Task<bool> CheckHealthAsync(CancellationToken ct) =>
        IsProvisioned
            ? AssistantHealthProbe.CheckAsync(this, ct, _logger, ProbeTimeout)
            : Task.FromResult(false);

    // Recycle pooled connections so a process-lifetime singleton never pins a stale one (the
    // documented long-lived-HttpClient alternative to IHttpClientFactory). Largely moot for a
    // localhost leaf, but correct and explicit about intent.
    private static SocketsHttpHandler NewHandler() => new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    };
}
