namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// <c>GET /accounts/names?id=…</c> — the username behind each account id asked about, read from this
/// node's replica of the cluster's accounts.
/// </summary>
/// <remarks>
/// A surface names a person by account id wherever it records who did something — the author of a
/// maintenance window list, of a reactor rule, of an automation setting — because an id is what an
/// access check takes. This is how a panel turns one into a name somebody can read. An id the replica
/// does not hold is absent from <see cref="Names"/>, never answered with a guess.
/// </remarks>
/// <param name="Names">Each known account id asked about, mapped to its username.</param>
public sealed record AccountNames(IReadOnlyDictionary<string, string> Names);
