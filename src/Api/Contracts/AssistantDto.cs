namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// The M7 assistant turn-relay request body (<c>POST /api/v1/assistant/turn</c>) — forwarded to the
/// assistant's own <c>/turn</c>: the user's <c>prompt</c>, an optional <c>think</c> toggle, and an
/// optional <c>tools</c> subset. The conversation <em>identity</em> (WHO the caller is) is still NOT
/// here — the API forwards the verified caller's Discord id (never a client-supplied one), so one
/// caller can't read or poison another's memory. The optional per-chat <see cref="ConversationId"/> is
/// a SUB-scope <em>within</em> that user's namespace, not an identity (see below).
/// </summary>
public sealed record AssistantTurnRequest(
    string? Prompt,
    bool? Think = null,
    IReadOnlyList<string>? Tools = null,
    // The per-turn "let the assistant act" toggle from the SPA chat. INTENT only — the API folds it
    // with the caller's verified tier (operator+) and forwards a single trusted decision to the
    // assistant (X-Relay-Can-Act). A viewer setting this true cannot escalate: the tier gate zeroes it,
    // and command execution itself is still the operator-gated M3 path (fork (a)).
    bool? Actions = null,
    // The per-CHAT conversation id from the SPA ("new chat" = new id). Forwarded to the assistant as a
    // SUB-scope of the verified caller's memory key (web:<userId>:<conversationId>) so each chat is a
    // fresh context window. It does NOT carry identity — the user-id prefix is always the API's verified,
    // server-derived Discord id, never client-supplied — so a client-chosen id can only ever partition
    // its OWN history, never read or poison another caller's. Sanitised ([A-Za-z0-9_-], capped) before
    // forwarding; null/blank ⇒ the bare per-user conversation (the prior single-context behaviour).
    string? ConversationId = null);
