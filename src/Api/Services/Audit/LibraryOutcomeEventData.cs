using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.Api.Services.Audit;

/// <summary>
/// Data for <c>library.renamed</c> / <c>library.failed</c> — a library mutation this API issued that the
/// engine itself records nothing about.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only the half kgsm is silent on.</b> Registering and deregistering a root emit
/// <c>library.added</c>/<c>library.removed</c>, so those rows ride the engine's own events and nothing is
/// written here for them. A rename touches the registry and the marker and emits nothing, and a mutation
/// the engine refuses exits non-zero and emits nothing — for both, this is the only record there can be.
/// </para>
/// <para>
/// One payload for both types, told apart by which type fired — the <see cref="CommandOutcomeEventData"/>
/// pattern, and the reason these publish off the raw hook rather than a typed handler.
/// </para>
/// <para>
/// <b>Nothing here is composed.</b> <see cref="Error"/> is the engine's own stderr and
/// <see cref="ExitCode"/> its own number. A refused removal names the instances that blocked it in that
/// stderr, and rewording it would throw away the only part an operator needs.
/// </para>
/// </remarks>
public sealed class LibraryOutcomeEventData : LibraryEventDataBase
{
    /// <summary>Gets or sets which mutation was asked for (<c>add</c>, <c>remove</c>, <c>rename</c>).</summary>
    public string Verb { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the root's absolute path, when the request named one.
    /// </summary>
    /// <remarks>Null on a rename and on a removal — neither names a path, and filling one in would report
    /// a lookup that did not happen.</remarks>
    public string? Path { get; set; }

    /// <summary>Gets or sets the name a rename moved the library to. Null for the other verbs.</summary>
    public string? NewName { get; set; }

    /// <summary>Gets or sets the engine's failure detail, verbatim. Null on a successful rename.</summary>
    public string? Error { get; set; }

    /// <summary>Gets or sets the engine's exit code, when a process ran to produce one.</summary>
    public int? ExitCode { get; set; }
}
