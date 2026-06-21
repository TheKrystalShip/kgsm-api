namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// Root payload for <c>GET /api/v1</c> — the frontend's connectivity handshake and
/// version check ("reach the API" in M0). Carries a timestamp so a real versioned
/// route also exercises the ISO-8601-UTC-<c>Z</c> serialization convention.
/// </summary>
public sealed record ApiInfo(string Name, string Version, DateTimeOffset Time)
{
    /// <summary>
    /// The in-process API version string — the single honest value this build can report
    /// (there is no <c>&lt;Version&gt;</c> in the csproj, so a semver would be fabricated).
    /// Shared so the <c>GET /api/v1</c> root (<c>MetaController</c>) and the Host DTO's
    /// <c>panelVersion</c> can never drift to different values.
    /// </summary>
    public const string ApiVersion = "v1";
}
