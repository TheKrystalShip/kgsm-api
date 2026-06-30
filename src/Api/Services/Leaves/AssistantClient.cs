using System.Net.Http.Json;
using System.Net.Sockets;

namespace TheKrystalShip.Api.Services.Leaves;

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

    private readonly ILogger<AssistantClient> _logger;
    private readonly LeafRegistry _registry;
    private readonly string _relaySecret;
    private readonly bool _hasBaseUrl;

    public AssistantClient(ApiOptions options, LeafRegistry registry, ILogger<AssistantClient> logger)
        : base(NewHandler(), disposeHandler: true)
    {
        _logger = logger;
        _registry = registry;
        _relaySecret = options.AssistantRelaySecret;

        // Set the base address from the configured URL whenever one is present (independent of the runtime
        // provisioning flag) so a connect/disconnect arms/disarms the client live without a restart. Without
        // a configured URL there is no endpoint to flip to (no universal default), so the runtime flip can
        // only ever report the capability down — the honest limit, noted in the feature plan.
        if (Uri.TryCreate(options.AssistantBaseUrl, UriKind.Absolute, out Uri? baseUri))
        {
            BaseAddress = baseUri;
            _hasBaseUrl = true;
        }
    }

    /// <summary>True when the assistant is provisioned (connected) on this host at runtime AND a base URL is
    /// configured to reach it. The capability/relay calls all gate on this, so disconnecting the assistant
    /// disarms them live.</summary>
    public bool IsProvisioned => _hasBaseUrl && _registry.IsProvisioned(ProvisionableLeaf.Assistant);

    /// <summary>
    /// Opens the assistant's <c>POST /turn</c> as an SSE stream on a verified end-user's behalf (M7):
    /// posts <paramref name="turnBody"/> with <c>Accept: text/event-stream</c> and the trusted-relay
    /// headers — the shared <c>X-Relay-Secret</c>, the forwarded Discord identity (<c>X-Relay-User</c>
    /// / <c>X-Relay-User-Name</c>), and the API's per-turn action-authority decision
    /// (<c>X-Relay-Can-Act</c>, from <paramref name="canAct"/>, the authority to PROPOSE; and
    /// <c>X-Relay-Auto-Act</c>, from <paramref name="autoAct"/>, the admin-only authority to AUTO-RUN
    /// lifecycle commands without confirmation), and the optional per-chat <c>X-Relay-Conversation-Id</c>
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
        object turnBody, string relayUserId, string relayDisplayName, bool canAct, bool autoAct,
        string? conversationId, CancellationToken ct)
    {
        if (!IsProvisioned)
            return null;

        var request = new HttpRequestMessage(HttpMethod.Post, "/turn")
        {
            Content = JsonContent.Create(turnBody),
        };
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (!string.IsNullOrEmpty(_relaySecret))
            request.Headers.TryAddWithoutValidation("X-Relay-Secret", _relaySecret);
        // The API's PROPOSE authority for this turn (operator+ tier). The assistant trusts it ONLY
        // because X-Relay-Secret matched; "false" (or absent) never grants.
        request.Headers.TryAddWithoutValidation("X-Relay-Can-Act", canAct ? "true" : "false");
        // The API's AUTO-ACCEPT authority (admin tier ∧ the user's toggle): when "true" the assistant
        // runs lifecycle commands immediately instead of staging them. Strictly stronger than can-act;
        // same trust basis and same fail-closed default.
        request.Headers.TryAddWithoutValidation("X-Relay-Auto-Act", autoAct ? "true" : "false");
        // The user id is a Discord snowflake the API set at login (not free text); the display name is a
        // user-controlled Discord value crossing a trust boundary, so strip control chars (CR/LF) — defense
        // in depth against header injection, and it also avoids a weird display name throwing on send.
        request.Headers.TryAddWithoutValidation("X-Relay-User", HeaderSafe(relayUserId));
        string displayName = HeaderSafe(relayDisplayName);
        if (!string.IsNullOrEmpty(displayName))
            request.Headers.TryAddWithoutValidation("X-Relay-User-Name", displayName);
        // The per-chat conversation id — a SUB-scope of THIS user's assistant memory
        // (web:<userId>:<id>), so a "new chat" in the SPA is a fresh context window. NOT an identity:
        // the assistant always prefixes the verified X-Relay-User, so this can only partition the
        // caller's own history. Already bounded to [A-Za-z0-9_-] by the controller; omitted when null ⇒
        // the assistant keeps the bare per-user conversation (the prior single-context behaviour).
        string conv = HeaderSafe(conversationId ?? string.Empty);
        if (!string.IsNullOrEmpty(conv))
            request.Headers.TryAddWithoutValidation("X-Relay-Conversation-Id", conv);

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
        string relayUserId, string relayDisplayName, CancellationToken ct) =>
        RelaySendAsync(HttpMethod.Get, "/conversations", relayUserId, relayDisplayName, ct);

    /// <summary>
    /// Loads one of the verified end-user's conversations: <c>GET /conversations/{chatId}</c> on their
    /// behalf with the trusted-relay identity. The assistant composes the full key
    /// (<c>web:{userId}:{chatId}</c>) server-side, so <paramref name="chatId"/> can only ever address the
    /// caller's OWN conversation. The body is the assistant's transcript JSON verbatim. Returns
    /// <see langword="null"/> when the assistant isn't provisioned; the caller <strong>owns disposal</strong>.
    /// </summary>
    public Task<HttpResponseMessage?> GetConversationAsync(
        string relayUserId, string relayDisplayName, string chatId, CancellationToken ct) =>
        RelaySendAsync(HttpMethod.Get, $"/conversations/{Uri.EscapeDataString(chatId)}", relayUserId, relayDisplayName, ct);

    /// <summary>
    /// Soft-deletes one of the verified end-user's conversations: <c>DELETE /conversations/{chatId}</c> on
    /// their behalf with the trusted-relay identity. The assistant composes the full key
    /// (<c>web:{userId}:{chatId}</c>) server-side, so <paramref name="chatId"/> can only ever address the
    /// caller's OWN conversation; it appends a tombstone — the transcript (the corpus) is retained, only the
    /// listing hides it. The upstream answers <c>204</c>. Returns <see langword="null"/> when the assistant
    /// isn't provisioned; the caller <strong>owns disposal</strong>.
    /// </summary>
    public Task<HttpResponseMessage?> DeleteConversationAsync(
        string relayUserId, string relayDisplayName, string chatId, CancellationToken ct) =>
        RelaySendAsync(HttpMethod.Delete, $"/conversations/{Uri.EscapeDataString(chatId)}", relayUserId, relayDisplayName, ct);

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
        string relayUserId, string relayDisplayName, string chatId, CancellationToken ct) =>
        RelaySendAsync(HttpMethod.Post, $"/conversations/{Uri.EscapeDataString(chatId)}/compact", relayUserId, relayDisplayName, ct);

    // Shared relay-on-the-user's-behalf (GET read / DELETE soft-delete): forwards the secret + forwarded
    // identity (these endpoints need no can-act/auto-act decision), reads the small body fully. Left to the
    // default Timeout — these are short request/response calls, not the long SSE stream.
    private async Task<HttpResponseMessage?> RelaySendAsync(
        HttpMethod method, string path, string relayUserId, string relayDisplayName, CancellationToken ct)
    {
        if (!IsProvisioned)
            return null;

        var request = new HttpRequestMessage(method, path);
        request.Headers.Accept.ParseAdd("application/json");
        if (!string.IsNullOrEmpty(_relaySecret))
            request.Headers.TryAddWithoutValidation("X-Relay-Secret", _relaySecret);
        request.Headers.TryAddWithoutValidation("X-Relay-User", HeaderSafe(relayUserId));
        string displayName = HeaderSafe(relayDisplayName);
        if (!string.IsNullOrEmpty(displayName))
            request.Headers.TryAddWithoutValidation("X-Relay-User-Name", displayName);

        return await SendAsync(request, ct).ConfigureAwait(false);
    }

    // Drop control chars (incl. CR/LF) so a user-controlled value can never split a header.
    private static string HeaderSafe(string value) =>
        string.IsNullOrEmpty(value) ? value : new string(value.Where(c => !char.IsControl(c)).ToArray());

    /// <summary>
    /// Liveness probe for the §4·b assistant capability: <c>GET /health</c>, a 2xx means the assistant
    /// is up and able to provide its capability (the canonical signal polled frequently by
    /// <c>LeafHealthMonitor</c>). Returns <c>false</c> on timeout, unreachable, or non-2xx — it never
    /// throws, and never blocks longer than <see cref="ProbeTimeout"/>. The assistant already serves
    /// <c>/health</c> (the ecosystem-standard path); the SSE turn relay still lands at M7.
    /// </summary>
    public async Task<bool> CheckHealthAsync(CancellationToken ct)
    {
        if (!IsProvisioned)
            return false;

        // Self-bound to the probe budget, independent of the client's (default) Timeout, while
        // still honoring caller cancellation through the linked token.
        using var timed = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timed.CancelAfter(ProbeTimeout);
        try
        {
            // Headers-only: we only need the status, not the body.
            using HttpResponseMessage resp = await this
                .GetAsync("/health", HttpCompletionOption.ResponseHeadersRead, timed.Token)
                .ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("assistant /health probe timed out after {Timeout}", ProbeTimeout);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "assistant /health probe failed");
            return false;
        }
    }

    // Recycle pooled connections so a process-lifetime singleton never pins a stale one (the
    // documented long-lived-HttpClient alternative to IHttpClientFactory). Largely moot for a
    // localhost leaf, but correct and explicit about intent.
    private static SocketsHttpHandler NewHandler() => new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    };
}
