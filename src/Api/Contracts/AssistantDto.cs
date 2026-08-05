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
    string? ConversationId = null,
    // The CURRENT content of a blueprint draft the user is reviewing in the chat editor, when this turn
    // carries one (the SPA sends what's in the Monaco card, edits included). Forwarded verbatim in the turn
    // body so the assistant can offer revise_blueprint and let the model change the draft from chat. Not
    // identity-bearing; re-validated by the assistant when a revision is saved. Null on an ordinary turn.
    string? DraftYaml = null);

/// <summary>
/// The assistant confirm-relay request body (<c>POST /api/v1/assistant/confirm</c>) — forwarded verbatim
/// to the assistant's own <c>/confirm</c>. Confirms a staged action the assistant proposed in a turn (the
/// operator-gated resume). <see cref="Token"/> is the host-minted, single-use confirmation token from the
/// <c>command.proposed</c> frame (opaque to the API); <see cref="EditedContent"/> carries an edited body
/// where the confirmation kind allows one (the blueprint-review card's Monaco YAML) — null for a plain
/// confirm. The API forwards these unchanged; the assistant owns validation, authority (its own bot
/// lookup), and the whole finalize pipeline.
/// </summary>
public sealed record AssistantConfirmRequest(
    string? Token,
    string? EditedContent = null);

/// <summary>
/// The turn-feedback request body (<c>POST /api/v1/assistant/conversations/{id}/turns/{turnId}/feedback</c>)
/// — how the caller judged one of their own answers, forwarded verbatim to the assistant.
/// <see cref="Rating"/> is <c>"up"</c>, <c>"down"</c>, or <c>null</c> to withdraw a verdict already left.
/// <see cref="Note"/> is the optional "what went wrong" behind a thumbs-down; the assistant discards one
/// sent with a thumbs-up, since a complaint filed against an answer its reader called fine is not
/// something any reader could act on.
/// </summary>
public sealed record TurnFeedbackRequest(
    string? Rating,
    string? Note = null);
