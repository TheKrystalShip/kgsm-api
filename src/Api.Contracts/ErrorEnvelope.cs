using System.Text.Json;
using System.Text.Json.Serialization;

namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// The frontend-facing error contract, frozen verbatim from architecture.html §6
/// (Conventions → Errors): <c>{ "error": { "code", "message", "details?" } }</c>,
/// alongside a standard HTTP status. The SPA's API client normalizes this into a
/// thrown <c>ApiError</c>, so the shape is load-bearing — additive changes only.
/// </summary>
public sealed record ErrorEnvelope(ErrorBody Error);

/// <summary>
/// The body of an <see cref="ErrorEnvelope"/>. <paramref name="Code"/> is a stable,
/// machine-matchable string (e.g. <c>not_found</c>, <c>internal_error</c>);
/// <paramref name="Message"/> is human-readable; <paramref name="Details"/> is
/// optional arbitrary JSON, omitted from the wire when null.
/// </summary>
public sealed record ErrorBody(
    string Code,
    string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    JsonElement? Details = null);
