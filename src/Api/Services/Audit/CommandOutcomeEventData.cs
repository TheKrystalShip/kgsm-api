using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.Api.Services.Audit;

/// <summary>
/// Data for <c>command.failed</c> / <c>command.refused</c> / <c>command.cancelled</c> — a command this
/// API issued that ended without doing the thing.
/// </summary>
/// <remarks>
/// <para>
/// One payload for all three types, told apart by which type fired. They carry the same facts — which
/// verb, on which instance, under which job — and differ only in how the attempt ended, which is what
/// the event type already says. It is also why these publish off the raw hook rather than a typed
/// handler: kgsm-lib keys a typed handler on the payload CLASS, and one registration for three types
/// could not tell which arrived.
/// </para>
/// <para>
/// ⚠ <b>Nothing here is composed.</b> <see cref="Error"/> is the engine's own stderr and
/// <see cref="ExitCode"/> its own number; neither is reworded, and a run that said nothing leaves both
/// null rather than gaining a sentence this API wrote.
/// </para>
/// </remarks>
public sealed class CommandOutcomeEventData : EventDataBase
{
    /// <summary>Gets or sets the verb that was asked for (<c>start</c>, <c>backup_create</c>, …).</summary>
    public string Verb { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the job the command ran as.
    /// </summary>
    /// <remarks>
    /// Populatable here where an engine echo's is not: no id round-trips the stateless engine, but this
    /// row is written by the process that owns the job, so naming it is reporting what it holds rather
    /// than reconstructing something.
    /// </remarks>
    public string? JobId { get; set; }

    /// <summary>Gets or sets the batch that issued it, when one did.</summary>
    public string? BatchId { get; set; }

    /// <summary>Gets or sets the engine's failure detail, verbatim.</summary>
    public string? Error { get; set; }

    /// <summary>Gets or sets the engine's exit code, when a process ran to produce one.</summary>
    public int? ExitCode { get; set; }

    /// <summary>
    /// Gets or sets the library the instance was in when a <c>move</c> was asked for.
    /// </summary>
    /// <remarks>
    /// Only a move names either library; every other verb leaves both null. They are here because the
    /// engine's <c>server.moved</c> carries them for a move that WORKED, and a refused one — the disk
    /// somebody was trying to empty, and the disk it could not be emptied onto — is exactly the record
    /// they go looking for afterwards. Null when the roster could not say where the instance was, which
    /// is a real unknown rather than a blank to fill.
    /// </remarks>
    public string? FromLibrary { get; set; }

    /// <summary>Gets or sets the library a <c>move</c> was asked to put it in.</summary>
    public string? ToLibrary { get; set; }
}
