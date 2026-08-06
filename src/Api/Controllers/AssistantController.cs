using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Leaves;

using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Discord;

using TheKrystalShip.KGSM.Auth.Sessions;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// The <c>/assistant</c> AI surface (M7, architecture.html §5·a; keystone O1). <c>POST
/// /api/v1/assistant/turn</c> relays the per-host assistant leaf's <c>/turn</c> SSE to the SPA
/// <strong>near-verbatim</strong> — the assistant already emits the canonical §5·a typed events
/// (<c>text.delta</c>/<c>tool.start</c>/<c>tool.result</c>/<c>command.proposed</c>/<c>done</c>/
/// <c>error</c>, + opt-in <c>thinking.delta</c>), so the API shapes nothing; it authenticates,
/// capability-gates, and streams.
/// <para>
/// <b>Auth:</b> gated at <b>viewer</b> — a turn is a chat/read that may <em>propose</em> a command
/// but never executes one (execution is the operator-gated M3 command path; <c>command.verified</c>
/// is the SPA's to compose, NOT a turn event — see <c>kgsm-llm/docs/m7-sse-5a-spec.md</c>). The API
/// forwards the verified caller's Discord identity to the assistant over the trusted co-located relay
/// (a shared secret); the assistant still derives authority itself from the bot, and keys per-user
/// memory on the forwarded id.
/// </para>
/// <para>
/// <b>Degrade gracefully:</b> assistant absent on this host ⇒ <c>404</c>; provisioned-but-down ⇒
/// <c>503</c>; reachable-but-rejecting (a relay misconfig) ⇒ <c>502</c> — all the frozen <c>{error}</c>
/// envelope, decided <em>before</em> the SSE response is committed.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/assistant")]
[Authorize(Policy = AuthPolicy.Viewer)]
public sealed class AssistantController(
    AssistantClient assistant,
    LeafHealthMonitor health,
    ILogger<AssistantController> logger) : ControllerBase
{
    /// <summary>
    /// <c>POST /api/v1/assistant/turn</c> — body <c>{ prompt, think?, tools? }</c>. On success the
    /// response is <c>text/event-stream</c>, relaying the assistant's §5·a frames verbatim until the
    /// turn's terminal <c>done</c>/<c>error</c>. A client disconnect tears the whole chain down (the
    /// assistant aborts generation). Errors before the stream commits use the <c>{error}</c> envelope.
    /// </summary>
    [HttpPost("turn")]
    public async Task<IActionResult> Turn([FromBody] AssistantTurnRequest? body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body?.Prompt))
            return Error(StatusCodes.Status400BadRequest, "bad_request", "prompt is required");

        // Capability gate (degrade gracefully) — read the always-on LeafHealthMonitor, the same source
        // the §4·b capability block reports from, so the relay and the capability dial never disagree.
        Capability cap = health.Current.Assistant;
        if (!cap.Provisioned)
            return Error(StatusCodes.Status404NotFound, "not_found", "no assistant on this host");
        if (cap.Status != CapabilityStatus.Operational)
            return Error(StatusCodes.Status503ServiceUnavailable, "unavailable", "the assistant is currently unavailable");

        // Forward the verified caller's Discord identity (NEVER client-supplied): the assistant builds
        // its principal from it and keys per-user memory on web:<userId>.
        ClaimsIdentity? ci = User.Identity as ClaimsIdentity;
        DiscordIdentity? identity = ci is not null ? SessionClaims.ReadIdentity(ci) : null;
        if (identity is null)
            return Error(StatusCodes.Status401Unauthorized, "unauthorized", "no verified identity");

        // The caller's VERIFIED tier travels with their identity, and it is the assistant's whole
        // authority on the relay path — that surface does no Discord lookup for a relayed caller.
        // Proposing a command is a tier capability (operator+, toggle-independent; the user still
        // confirms each one), so nothing here needs to pre-compute it.
        //
        // autoAct is the exception and stays its own decision: may the assistant AUTO-RUN lifecycle
        // commands with no confirmation? admin ONLY, AND the user turned the toggle on. body.Actions is
        // the toggle INTENT — it can only ever narrow this, never widen authority, so an operator (or a
        // tampered SPA forcing actions:true) is zeroed here.
        KgsmTier tier = ci is not null ? SessionClaims.ReadTier(ci) : KgsmTier.None;
        var caller = RelayPrincipal.From(identity, tier);
        bool autoAct = (body.Actions ?? false) && tier >= KgsmTier.Admin;

        // The per-chat conversation id from the SPA (the "new chat" identity). Forwarded to the assistant
        // as a SUB-scope of the verified user's memory key (web:<userId>:<chatId>) so each chat is a fresh
        // context window; the user-id prefix stays server-authoritative, so this can never cross users.
        string? conversationId = SanitizeConversationId(body.ConversationId);

        var turnBody = new { prompt = body!.Prompt, think = body.Think, tools = body.Tools, draftYaml = body.DraftYaml };

        HttpResponseMessage? upstream;
        try
        {
            upstream = await assistant.OpenTurnStreamAsync(turnBody, caller, autoAct, conversationId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new EmptyResult(); // caller went away before the stream opened
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "assistant turn relay: upstream connect failed");
            return Error(StatusCodes.Status502BadGateway, "bad_gateway", "the assistant could not be reached");
        }

        if (upstream is null) // not provisioned (race with the capability gate) — same honest 404
            return Error(StatusCodes.Status404NotFound, "not_found", "no assistant on this host");

        using (upstream)
        {
            if (!upstream.IsSuccessStatusCode)
            {
                logger.LogWarning("assistant turn relay: upstream returned {Status}", (int)upstream.StatusCode);
                return Error(StatusCodes.Status502BadGateway, "bad_gateway", "the assistant rejected the relay");
            }

            // Commit the SSE response and relay frames verbatim. Buffering off so each frame reaches the
            // client promptly; the upstream is disposed on the way out (or on disconnect) → the assistant
            // aborts generation. After this point the status is committed — any failure ends the stream.
            Response.StatusCode = StatusCodes.Status200OK;
            Response.ContentType = "text/event-stream";
            Response.Headers.CacheControl = "no-cache";
            Response.Headers["X-Accel-Buffering"] = "no";
            HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

            try
            {
                await using Stream stream = await upstream.Content.ReadAsStreamAsync(ct);
                await stream.CopyToAsync(Response.Body, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Client disconnected mid-stream; disposing `upstream` aborts the assistant. Nothing to write.
            }
            catch (Exception ex)
            {
                // The response is already committed (200 + partial frames), so we can't change the status —
                // the assistant surfaces its own failures as the in-band `error` event; a transport drop just
                // ends the stream. Log and finish.
                logger.LogWarning(ex, "assistant turn relay: stream copy ended abnormally");
            }
        }

        return new EmptyResult();
    }

    /// <summary>
    /// <c>POST /api/v1/assistant/confirm</c> — body <c>{ token, editedContent? }</c>. Finalizes a staged
    /// action the assistant proposed in a turn (today: the blueprint-review Save — the human's edited YAML
    /// rides <c>editedContent</c>). <b>Operator</b>-gated (this EXECUTES a mutation — install/verify —
    /// unlike the viewer-gated turn/reads); the assistant additionally re-derives authority from the bot,
    /// so authority is checked on both sides.
    /// <para>
    /// A blueprint finalize is minutes of test-install → verify → repair, so — exactly like the turn — this
    /// is an <c>text/event-stream</c> relay: the assistant streams <c>progress</c> steps + keep-alive
    /// heartbeats through the long silent stretches and a terminal <c>result</c> frame carrying the
    /// <c>ConfirmResponse</c> (rich card + any re-edit token — all the assistant's schema, relayed verbatim).
    /// Buffered into one response, that multi-minute silence would let an idle-connection reaper on a remote
    /// path drop the socket, leaving the chat card spinning with no result. Same degrade gate as the turn
    /// (absent → 404, down → 503, upstream reject → 502) decided BEFORE the SSE commits; after that any
    /// failure ends the stream. A client disconnect tears the whole chain down.
    /// </para>
    /// </summary>
    [HttpPost("confirm")]
    [Authorize(Policy = AuthPolicy.Operator)]
    public async Task<IActionResult> Confirm([FromBody] AssistantConfirmRequest? body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body?.Token))
            return Error(StatusCodes.Status400BadRequest, "bad_request", "token is required");

        Capability cap = health.Current.Assistant;
        if (!cap.Provisioned)
            return Error(StatusCodes.Status404NotFound, "not_found", "no assistant on this host");
        if (cap.Status != CapabilityStatus.Operational)
            return Error(StatusCodes.Status503ServiceUnavailable, "unavailable", "the assistant is currently unavailable");

        ClaimsIdentity? ci = User.Identity as ClaimsIdentity;
        DiscordIdentity? identity = ci is not null ? SessionClaims.ReadIdentity(ci) : null;
        if (identity is null)
            return Error(StatusCodes.Status401Unauthorized, "unauthorized", "no verified identity");

        // This action is operator-gated, so the forwarded tier is operator or better — but it is read,
        // never assumed, so an admin confirming is relayed as an admin.
        var caller = RelayPrincipal.From(identity, SessionClaims.ReadTier(ci!));

        HttpResponseMessage? upstream;
        try
        {
            upstream = await assistant.ConfirmAsync(caller, body, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new EmptyResult(); // caller went away before the stream opened
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "assistant confirm relay: upstream connect failed");
            return Error(StatusCodes.Status502BadGateway, "bad_gateway", "the assistant could not be reached");
        }

        if (upstream is null) // not provisioned (race with the capability gate) — same honest 404
            return Error(StatusCodes.Status404NotFound, "not_found", "no assistant on this host");

        using (upstream)
        {
            if (!upstream.IsSuccessStatusCode)
            {
                logger.LogWarning("assistant confirm relay: upstream returned {Status}", (int)upstream.StatusCode);
                return Error(StatusCodes.Status502BadGateway, "bad_gateway", "the assistant rejected the relay");
            }

            // Commit the SSE response and relay the finalize frames verbatim. Buffering off so each progress
            // frame + heartbeat reaches the client promptly; upstream disposed on the way out (or on
            // disconnect) → the assistant tears the finalize down. After this the status is committed.
            Response.StatusCode = StatusCodes.Status200OK;
            Response.ContentType = "text/event-stream";
            Response.Headers.CacheControl = "no-cache";
            Response.Headers["X-Accel-Buffering"] = "no";
            HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

            try
            {
                await using Stream stream = await upstream.Content.ReadAsStreamAsync(ct);
                await stream.CopyToAsync(Response.Body, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Client disconnected mid-stream; disposing `upstream` tears down the finalize. Nothing to write.
            }
            catch (Exception ex)
            {
                // The response is already committed (200 + partial frames), so the status can't change — the
                // assistant surfaces its own failures as the in-band `error`/`result` event; a transport drop
                // just ends the stream. Log and finish.
                logger.LogWarning(ex, "assistant confirm relay: stream copy ended abnormally");
            }
        }

        return new EmptyResult();
    }

    /// <summary>
    /// <c>GET /api/v1/assistant/conversations</c> — the verified caller's own past chats (the reverse
    /// path), <b>viewer</b>-gated. Relays the assistant's conversation-summary JSON verbatim so a fresh
    /// browser/device can show history that lives server-side, not only in the client. Same degrade gate
    /// as the turn (absent → 404, down → 503, upstream reject → 502).
    /// </summary>
    [HttpGet("conversations")]
    public Task<IActionResult> Conversations(CancellationToken ct) =>
        RelayAsync((c, ct2) => assistant.GetConversationsAsync(c, ct2), RelayedJson, ct);

    /// <summary>
    /// <c>GET /api/v1/assistant/conversations/{id}</c> — one of the caller's chats, full transcript,
    /// <b>viewer</b>-gated. The assistant scopes <c>{id}</c> under the forwarded user id, so it can only
    /// ever address the caller's OWN conversation. Relays the transcript JSON verbatim.
    /// </summary>
    [HttpGet("conversations/{id}")]
    public Task<IActionResult> Conversation(string id, CancellationToken ct) =>
        RelayAsync((c, ct2) => assistant.GetConversationAsync(c, id, ct2), RelayedJson, ct);

    /// <summary>
    /// <c>DELETE /api/v1/assistant/conversations/{id}</c> — soft-deletes one of the caller's chats,
    /// <b>viewer</b>-gated (deleting your OWN chat history is a personal read-surface action, not a
    /// privileged host action). The assistant scopes <c>{id}</c> under the forwarded user id (own-conversation
    /// only) and appends a tombstone — the transcript is retained, only the listing hides it. Returns <c>204</c>.
    /// </summary>
    [HttpDelete("conversations/{id}")]
    public Task<IActionResult> DeleteConversation(string id, CancellationToken ct) =>
        RelayAsync((c, ct2) => assistant.DeleteConversationAsync(c, id, ct2),
            _ => Task.FromResult<IActionResult>(NoContent()), ct);

    /// <summary>
    /// <c>POST /api/v1/assistant/conversations/{id}/compact</c> — compacts one of the caller's chats,
    /// <b>viewer</b>-gated (compacting your OWN conversation to free its context window is a personal action,
    /// not a privileged host action — like reading or deleting it). The assistant scopes <c>{id}</c> under the
    /// forwarded user id (own-conversation only) and summarises the history in place (non-destructive),
    /// returning a <c>CompactionOutcome</c> JSON relayed verbatim. No audit row — it is neither an engine
    /// action nor a host mutation.
    /// </summary>
    [HttpPost("conversations/{id}/compact")]
    public Task<IActionResult> CompactConversation(string id, CancellationToken ct) =>
        RelayAsync((c, ct2) => assistant.CompactConversationAsync(c, id, ct2), RelayedJson, ct);

    /// <summary>
    /// <c>POST /api/v1/assistant/conversations/{id}/turns/{turnId}/feedback</c> — how the caller judged
    /// one of their OWN answers, <b>viewer</b>-gated. Rating the reply you received is a personal action
    /// on your own conversation, exactly like reading, deleting or compacting it.
    /// <para>
    /// Note the tier deliberately: the admin review surface next door reads OTHER people's conversations
    /// and is read-only. This is the opposite direction — the person the answer was for, saying whether
    /// it helped — which is why it lives here rather than there, and why a reviewer's opinion is not
    /// collected through it. The assistant scopes <c>{id}</c> under the forwarded user id AND checks the
    /// turn belongs to it, so a tampered <c>turnId</c> answers <c>404</c> rather than rating a stranger's
    /// turn. No audit row: it is neither an engine action nor a host mutation.
    /// </para>
    /// </summary>
    [HttpPost("conversations/{id}/turns/{turnId:long}/feedback")]
    public Task<IActionResult> SetTurnFeedback(
        string id, long turnId, [FromBody] TurnFeedbackRequest? body, CancellationToken ct) =>
        RelayAsync(
            (c, ct2) => assistant.SetTurnFeedbackAsync(
                c, id, turnId, body?.Rating, body?.Note, ct2),
            _ => Task.FromResult<IActionResult>(NoContent()), ct);

    /// <summary>
    /// <c>GET /api/v1/assistant/admin/conversations/users</c> — everyone who has talked to this host's
    /// assistant, <b>admin</b>-gated. The index an administrator picks from to review how the assistant is
    /// answering and where it needs tuning.
    /// <para>
    /// <b>Admin, not operator:</b> every other conversation endpoint here reads the CALLER'S OWN history
    /// and is viewer-gated; these three read other people's, which is a different power from acting on a
    /// server. The caller's verified tier rides to the assistant as <c>X-Relay-Tier</c>, which that surface is
    /// fail-closed on — so an unauthorized caller is stopped here, and a relay that never asserts admin is
    /// stopped there.
    /// </para>
    /// <para>
    /// <b>No audit row.</b> A review read is not recorded (decision, 2026-08-05). Users are told their
    /// conversations may be reviewed — the SPA's assistant discloses it before the first message — and that
    /// disclosure, not a log of each read, is how this is kept honest.
    /// </para>
    /// </summary>
    [HttpGet("admin/conversations/users")]
    [Authorize(Policy = AuthPolicy.Admin)]
    public Task<IActionResult> ReviewUsers(CancellationToken ct) =>
        RelayAsync((c, ct2) => assistant.GetReviewUsersAsync(c, ct2), RelayedJson, ct);

    /// <summary>
    /// <c>GET /api/v1/assistant/admin/conversations/stats</c> — the corpus roll-up behind the assistant's
    /// operator overview, <b>admin</b>-gated like the rest of the review surface: outcome mix, answer-time
    /// distribution, per-tool behaviour, prompt-version buckets, context occupancy, daily volume, and the
    /// leaf's live runtime. Relayed verbatim — the assistant owns the schema, and it derives every figure
    /// from the same log the transcripts come from.
    /// </summary>
    [HttpGet("admin/conversations/stats")]
    [Authorize(Policy = AuthPolicy.Admin)]
    public Task<IActionResult> ReviewStats(CancellationToken ct) =>
        RelayAsync((c, ct2) => assistant.GetReviewStatsAsync(c, ct2), RelayedJson, ct);

    /// <summary>
    /// <c>GET /api/v1/assistant/admin/conversations?user={userId}</c> — one user's conversations,
    /// <b>admin</b>-gated. Soft-deleted ones are included and flagged by the assistant: the transcript was
    /// never erased, and a conversation someone hid is exactly what a tuning review wants to see.
    /// </summary>
    [HttpGet("admin/conversations")]
    [Authorize(Policy = AuthPolicy.Admin)]
    public Task<IActionResult> ReviewConversations([FromQuery] string? user, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(user))
            return Task.FromResult<IActionResult>(Error(StatusCodes.Status400BadRequest, "bad_request", "user is required"));

        return RelayAsync((c, ct2) => assistant.GetReviewConversationsAsync(c, user, ct2), RelayedJson, ct);
    }

    /// <summary>
    /// <c>GET /api/v1/assistant/admin/conversations/{handle}</c> — one conversation's transcript,
    /// <b>admin</b>-gated. <paramref name="handle"/> is the opaque id the assistant's listing minted; the
    /// API forwards it untouched (it is the leaf's id scheme, not this API's), and the assistant refuses one
    /// that names anything it did not list. The transcript JSON is relayed verbatim.
    /// </summary>
    [HttpGet("admin/conversations/{handle}")]
    [Authorize(Policy = AuthPolicy.Admin)]
    public Task<IActionResult> ReviewConversation(string handle, CancellationToken ct) =>
        RelayAsync((c, ct2) => assistant.GetReviewConversationAsync(c, handle, ct2), RelayedJson, ct);

    // Shared relay core: the same capability + identity gates as the turn, then the caller-supplied
    // projection of a SUCCESSFUL upstream response (verbatim JSON for the reads, 204 for the delete).
    // Unlike the SSE turn, nothing is committed before we know the upstream status, so a real status code
    // is returned on every branch.
    private async Task<IActionResult> RelayAsync(
        Func<RelayPrincipal, CancellationToken, Task<HttpResponseMessage?>> call,
        Func<HttpResponseMessage, Task<IActionResult>> onSuccess,
        CancellationToken ct)
    {
        Capability cap = health.Current.Assistant;
        if (!cap.Provisioned)
            return Error(StatusCodes.Status404NotFound, "not_found", "no assistant on this host");
        if (cap.Status != CapabilityStatus.Operational)
            return Error(StatusCodes.Status503ServiceUnavailable, "unavailable", "the assistant is currently unavailable");

        ClaimsIdentity? ci = User.Identity as ClaimsIdentity;
        DiscordIdentity? identity = ci is not null ? SessionClaims.ReadIdentity(ci) : null;
        if (identity is null)
            return Error(StatusCodes.Status401Unauthorized, "unauthorized", "no verified identity");

        // Read the tier here rather than at each call site: the relayed calls are a mix of self-service
        // and admin-gated, and picking the authority per call is how one of them ends up asserting an
        // authority its own action does not require.
        var caller = RelayPrincipal.From(identity, SessionClaims.ReadTier(ci!));

        HttpResponseMessage? upstream;
        try
        {
            upstream = await call(caller, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new EmptyResult(); // caller went away
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "assistant conversation relay: upstream call failed");
            return Error(StatusCodes.Status502BadGateway, "bad_gateway", "the assistant could not be reached");
        }

        if (upstream is null)
            return Error(StatusCodes.Status404NotFound, "not_found", "no assistant on this host");

        using (upstream)
        {
            // An upstream 404 is an ANSWER, not a transport failure: the assistant reached a verdict —
            // it holds no such conversation. Flattening it into 502 would tell the client the leaf could
            // not be reached, which is false and unactionable. Every other non-2xx really is the relay
            // failing (a misconfigured secret, a rejected shape) and stays 502.
            if (upstream.StatusCode == System.Net.HttpStatusCode.NotFound)
                return Error(StatusCodes.Status404NotFound, "not_found", "no such conversation");

            if (!upstream.IsSuccessStatusCode)
            {
                logger.LogWarning("assistant conversation relay: upstream returned {Status}", (int)upstream.StatusCode);
                return Error(StatusCodes.Status502BadGateway, "bad_gateway", "the assistant rejected the relay");
            }

            return await onSuccess(upstream);
        }
    }

    // Project a successful upstream response by relaying its JSON body verbatim (the assistant owns the
    // schema; the API shapes nothing — same posture as the turn relay).
    private async Task<IActionResult> RelayedJson(HttpResponseMessage upstream)
    {
        string json = await upstream.Content.ReadAsStringAsync(HttpContext.RequestAborted);
        return Content(json, "application/json; charset=utf-8");
    }

    // The frozen { error: { code, message } } envelope (architecture.html §6) — only ever returned
    // before the SSE response is committed.
    private ObjectResult Error(int statusCode, string code, string message) =>
        StatusCode(statusCode, new ErrorEnvelope(new ErrorBody(code, message)));

    // Reduce a client-supplied chat id to a safe, bounded key segment ([A-Za-z0-9_-], ≤64 chars). The
    // assistant re-sanitises (it owns the key); this just keeps the forwarded header tidy and bounded so
    // a tampered client can't push an oversized/odd value. Blank/all-stripped ⇒ null ⇒ the assistant
    // uses the bare per-user key (one conversation), preserving the pre-fix behaviour.
    private static string? SanitizeConversationId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        Span<char> buf = stackalloc char[64];
        int n = 0;
        foreach (char c in raw)
        {
            if (n == buf.Length)
                break;
            if (char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_')
                buf[n++] = c;
        }
        return n == 0 ? null : new string(buf[..n]);
    }
}
