namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// Root payload for <c>GET /api/v1</c> — the frontend's connectivity handshake and
/// version check ("reach the API" in M0). Carries a timestamp so a real versioned
/// route also exercises the ISO-8601-UTC-<c>Z</c> serialization convention.
/// </summary>
public sealed record ApiInfo(string Name, string Version, DateTimeOffset Time);
