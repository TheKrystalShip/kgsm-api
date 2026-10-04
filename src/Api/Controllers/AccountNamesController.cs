using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Auth.Access;
using TheKrystalShip.Auth.Cluster;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// <c>GET /api/v1/accounts/names?id=…</c> — the usernames behind account ids, from this node's replica.
/// </summary>
/// <remarks>
/// <para>
/// Surfaces record a person by account id: the engine beside a maintenance window list, the reactor on a
/// rule, a leaf beside an automation setting. Those reach the panel verbatim from whoever recorded them,
/// so the name is resolved here, in one place, rather than by every component that records an author.
/// </para>
/// <para>
/// <b>Gated on being signed in, and on nothing else.</b> A caller asks about an id a surface it can read
/// already showed it, and an account id is opaque and unguessable, so a name answered here tells them who
/// stands behind something they were shown and nothing more. Only ids that were asked about are answered:
/// there is no listing.
/// </para>
/// </remarks>
[ApiController]
[Route("api/v1/accounts/names")]
[Authorize]
public sealed class AccountNamesController(MemberAccess access) : ControllerBase
{
    /// <summary>The most ids one request resolves — a page names a handful of people, never a directory.</summary>
    public const int MaxIds = 50;

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery(Name = "id")] string[]? ids, CancellationToken ct)
    {
        string[] asked = [.. (ids ?? []).Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i.Trim()).Distinct(StringComparer.Ordinal)];
        if (asked.Length > MaxIds)
            return BadRequest(new ErrorEnvelope(new ErrorBody("bad_request", $"at most {MaxIds} ids per request")));

        if (asked.Length == 0)
            return Ok(new AccountNames(new Dictionary<string, string>(StringComparer.Ordinal)));

        if (await access.EvaluatorAsync(ct).ConfigureAwait(false) is not { } evaluator)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ErrorEnvelope(new ErrorBody(
                "authority_unavailable", "This node's authority replica could not be read.")));
        }

        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string id in asked)
        {
            if (evaluator.Snapshot.Accounts.TryGetValue(id, out AccessAccount? account))
                names[id] = account.Name;
        }

        return Ok(new AccountNames(names));
    }
}
