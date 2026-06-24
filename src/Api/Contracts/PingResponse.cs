namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// The minimal payload for <c>GET /api/v1/ping</c> — a deliberately tiny, auth-free
/// timing target the SPA hits to measure <b>client-side</b> round-trip latency
/// (browser↔API). The server cannot observe the round trip, so it returns no number:
/// the body is just a liveness marker, and the latency is whatever the client clocks
/// between request and response (honest measurement, never a fabricated figure).
/// Kept smaller than <c>/health</c> and <c>/api/v1</c> on purpose — it must return
/// instantly (no DB/leaf access) so repeated polling stays cheap and the reading
/// reflects the link, not server work.
/// </summary>
public sealed record PingResponse(bool Pong);
