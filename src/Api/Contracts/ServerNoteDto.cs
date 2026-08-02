namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// The <c>GET/PUT/DELETE /servers/{id}/note</c> response body. <see cref="Note"/> is
/// <see langword="null"/> when the server has no note — the honest "nothing written", which is what
/// lets a surface distinguish an empty note from an unreachable engine.
/// </summary>
public sealed record ServerNoteView(string ServerId, ServerNote? Note);

/// <summary>
/// The request body for <c>PUT /servers/{id}/note</c>.
/// <list type="bullet">
///   <item><description><see cref="Body"/> — the note text. Required and non-empty: clearing is
///     <c>DELETE</c>, so an accidentally-emptied editor cannot silently wipe a note. Capped at
///     600 characters measured after sanitizing, and rejected rather than truncated when
///     longer.</description></item>
///   <item><description><see cref="Origin"/> — the driving surface (like <see cref="CommandRequest.Origin"/>),
///     stamped onto the engine call so the write is attributable. Absent ⇒ <c>api</c>.</description></item>
/// </list>
/// <para>The text is stored and returned verbatim; surfaces render it as plain text, never as markup,
/// because a note reaches player-facing surfaces.</para>
/// </summary>
public sealed record ServerNoteWrite(string? Body, string? Origin = null);

/// <summary>
/// The optional request body for <c>DELETE /servers/{id}/note</c> — carries only the driving surface,
/// so a clear is as attributable as a write. Absent ⇒ <c>api</c>.
/// </summary>
public sealed record ServerNoteClear(string? Origin = null);
