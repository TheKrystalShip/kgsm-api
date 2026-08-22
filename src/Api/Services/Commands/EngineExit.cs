namespace TheKrystalShip.Api.Services.Commands;

/// <summary>
/// kgsm exit codes this API acts on, rather than merely reports.
/// </summary>
/// <remarks>
/// <para>
/// Almost every engine failure is passed through as its own message and needs no name here — the API
/// does not interpret what went wrong, it says what the engine said. A code earns a constant only when
/// a caller has to <em>behave differently</em> because of it, and each one is a small coupling to the
/// engine's own numbering that has to be worth taking.
/// </para>
/// <para>
/// The alternative is matching the engine's message text, which is prose written for a person: it is
/// free to be reworded, translated or have a figure inserted, and a match on it would break silently
/// the first time somebody improved a sentence. An exit code is the part of a command's answer that is
/// meant to be read by a program.
/// </para>
/// </remarks>
internal static class EngineExit
{
    /// <summary>
    /// kgsm's <c>EC_INSUFFICIENT_MEMORY</c> (<c>core/errors.sh</c>): starting the instance would leave
    /// the node with less free memory than its configured floor.
    /// </summary>
    /// <remarks>
    /// A <b>refusal, not a failure</b>, and the distinction is what this constant exists for. Nothing is
    /// wrong with the instance — the node was full — so a surface that files it as a fault reads as
    /// blaming the server, and a retry policy that treats it as a fault will re-issue a command certain
    /// to be refused again until something else stops.
    /// </remarks>
    public const int InsufficientMemory = 51;
}
