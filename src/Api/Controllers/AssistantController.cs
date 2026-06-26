using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Leaves;

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

        // Resolve this turn's two action AUTHORITIES — the decision the API owns and the assistant
        // trusts (it never does its own Discord lookup on the relay path). Both are derived from the
        // caller's VERIFIED tier, never from the request body alone:
        //   • canAct  = may the assistant PROPOSE a command? operator+ (toggle-independent — proposing
        //               is a tier capability; the user still confirms each via the M3 command path).
        //   • autoAct = may it AUTO-RUN lifecycle commands with no confirmation? admin ONLY, AND the
        //               user turned the toggle on. This is the real gate: an operator (or a tampered
        //               SPA forcing actions:true) is zeroed here — autoAct requires admin tier.
        // body.Actions is the toggle INTENT; it can only ever narrow autoAct, never widen authority.
        AuthTier tier = ci is not null ? SessionClaims.ReadTier(ci) : AuthTier.None;
        bool canAct = tier >= AuthTier.Operator;
        bool autoAct = (body.Actions ?? false) && tier >= AuthTier.Admin;

        // The per-chat conversation id from the SPA (the "new chat" identity). Forwarded to the assistant
        // as a SUB-scope of the verified user's memory key (web:<userId>:<chatId>) so each chat is a fresh
        // context window; the user-id prefix stays server-authoritative, so this can never cross users.
        string? conversationId = SanitizeConversationId(body.ConversationId);

        var turnBody = new { prompt = body!.Prompt, think = body.Think, tools = body.Tools };

        HttpResponseMessage? upstream;
        try
        {
            upstream = await assistant.OpenTurnStreamAsync(turnBody, identity.UserId, identity.Display, canAct, autoAct, conversationId, ct);
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
