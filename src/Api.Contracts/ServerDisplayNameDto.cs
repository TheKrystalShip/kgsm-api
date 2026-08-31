namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// The <c>PUT/DELETE /servers/{id}/display-name</c> response body — the instance's id and the label it
/// reads by now, both re-read from the engine rather than echoed back from the request.
/// </summary>
/// <remarks>
/// <see cref="DisplayName"/> is never blank: an instance with no label of its own reads as its
/// <see cref="ServerId"/>, so a clear comes back reporting the id. The two fields are what a surface
/// needs to render the rename result honestly — the label it shows, and the id it keys on.
/// </remarks>
public sealed record ServerDisplayNameView(string ServerId, string DisplayName);

/// <summary>
/// The request body for <c>PUT /servers/{id}/display-name</c>.
/// <list type="bullet">
///   <item><description><see cref="DisplayName"/> — the new label. Required and non-empty: clearing is
///     <c>DELETE</c>, so an accidentally-emptied field cannot silently strip a server's name. Free text
///     otherwise — spaces, punctuation and emoji are all legal, because the label never reaches a path —
///     and capped at 200 characters measured after sanitizing, rejected rather than
///     truncated.</description></item>
///   <item><description><see cref="Origin"/> — the driving surface (like <see cref="CommandRequest.Origin"/>),
///     stamped onto the engine call so the rename is attributable. Absent ⇒ <c>api</c>.</description></item>
/// </list>
/// </summary>
public sealed record ServerDisplayNameWrite(string? DisplayName, string? Origin = null);

/// <summary>
/// The optional request body for <c>DELETE /servers/{id}/display-name</c> — carries only the driving
/// surface, so a clear is as attributable as a rename. Absent ⇒ <c>api</c>.
/// </summary>
public sealed record ServerDisplayNameClear(string? Origin = null);
