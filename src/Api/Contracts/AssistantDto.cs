namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// The M7 assistant turn-relay request body (<c>POST /api/v1/assistant/turn</c>) — forwarded to the
/// assistant's own <c>/turn</c>: the user's <c>prompt</c>, an optional <c>think</c> toggle, and an
/// optional <c>tools</c> subset. The conversation <em>identity</em> is deliberately NOT here — the
/// API forwards the verified caller's Discord id (never a client-supplied one), so one caller can't
/// read or poison another's assistant memory.
/// </summary>
public sealed record AssistantTurnRequest(
    string? Prompt,
    bool? Think = null,
    IReadOnlyList<string>? Tools = null);
