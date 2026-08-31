using System.Text.Json.Serialization;

namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// Root payload for <c>GET /api/v1</c> — the frontend's connectivity handshake and version check
/// ("reach the API" in M0). It doubles as the host's <strong>public identity card</strong>: this is the
/// one endpoint the multi-host SPA reaches <em>before login</em> (it's <c>[AllowAnonymous]</c>), so it
/// carries the low-sensitivity, operator-declared <see cref="Label"/>/<see cref="Region"/> + the real
/// <see cref="Build"/> so the connect screen can label "eu-west / Hotrod" with no extra fetch. The
/// runtime/OS detail (mild fingerprinting) stays behind auth on <c>GET /hosts/{id}</c> (see HostIdentity).
/// Carries a timestamp so a real versioned route also exercises the ISO-8601-UTC-<c>Z</c> convention.
/// </summary>
public sealed record ApiInfo(
    string Name,
    string Version,
    DateTimeOffset Time,
    // This build's real version (assembly informational version: <Version> + git SHA), distinct from the
    // route Version above. Always present.
    string Build,
    // The host's display label (operator-declared, defaults to the host id) — labels the connect screen.
    string Label,
    // The host's region — operator-declared free string; omitted when unset (honest unknown, never guessed).
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Region)
{
    /// <summary>
    /// The API <strong>route</strong> version — the path segment (<c>/api/v1</c>), an additive-versioning
    /// axis, NOT the build version (that is <see cref="Build"/>, sourced from the assembly). Shared so the
    /// <c>GET /api/v1</c> root (<c>MetaController</c>) and the Host DTO's <c>panelVersion</c> can never drift.
    /// </summary>
    public const string ApiVersion = "v1";
}
