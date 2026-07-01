using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Aggregation;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Players;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// The permanent player roster — <c>GET /servers/{id}/players</c> (player-presence-contract.md §5).
/// Reads the DB-backed <see cref="PlayerHistoryService"/> projection: ALL players who have ever
/// connected, each with their current status (online/offline/banned/unknown). The history is the
/// only authority — no separate "currently online" view.
/// </summary>
/// <remarks>
/// <para><b>Gated at operator</b> — a roster names real people/addresses, a step more sensitive
/// than a config value or a backup list.</para>
/// <para><b>Honest-unknown, never a fabricated empty.</b> <see cref="PlayersResponse.Detection"/> is
/// <see cref="PlayerDetection.Unknown"/> when the instance declares NEITHER
/// <c>Instance.PlayerJoinedRegex</c> nor <c>Instance.PlayerLeftRegex</c> — presence is unknowable, not
/// "nobody's here", so <see cref="PlayersResponse.Players"/> is forced to <c>[]</c> in that case
/// regardless of what the history projection holds for this id.</para>
/// </remarks>
[ApiController]
[Route("api/v1/servers/{id}/players")]
[Authorize(Policy = AuthPolicy.Operator)]
public sealed class ServerPlayersController(ServerAggregator aggregator, PlayerHistoryService history) : ControllerBase
{
    /// <summary>
    /// The permanent roster: <c>{ detection: "configured"|"unknown", players: [...] }</c>.
    /// Each player carries <c>status</c> (online/offline/banned/unknown), <c>firstSeen</c>,
    /// <c>lastSeen</c>, and optionally <c>banReason</c>.
    /// <list type="bullet">
    /// <item><c>404</c> — unknown server id.</item>
    /// <item><c>503</c> — the kgsm engine is not provisioned on this host.</item>
    /// <item><c>200</c> — <c>detection:"unknown"</c> + <c>players:[]</c> when the instance has no
    /// join/left detection configured; otherwise <c>detection:"configured"</c> + the full roster
    /// (possibly empty — no players have ever been observed).</item>
    /// </list>
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        if (HttpContext.RequestServices.GetService(typeof(IInstanceService)) is not IInstanceService instances)
            return Error(StatusCodes.Status503ServiceUnavailable, "unavailable",
                "the kgsm engine is not provisioned on this host");

        if (!await ExistsAsync(id, ct).ConfigureAwait(false))
            return NotFound();

        Instance? instance = instances.GetInstanceInfo(id);
        if (instance is null)
            return NotFound();

        bool unknown = string.IsNullOrEmpty(instance.PlayerJoinedRegex) && string.IsNullOrEmpty(instance.PlayerLeftRegex);
        if (unknown)
            return Ok(new PlayersResponse(PlayerDetection.Unknown, []));

        return Ok(new PlayersResponse(PlayerDetection.Configured, history.GetRoster(id)));
    }

    private async Task<bool> ExistsAsync(string id, CancellationToken ct)
    {
        IReadOnlyList<Server> servers = await aggregator.GetServersAsync(ct).ConfigureAwait(false);
        return servers.Any(s => string.Equals(s.Id, id, StringComparison.Ordinal));
    }

    private ObjectResult Error(int statusCode, string code, string message) =>
        StatusCode(statusCode, new ErrorEnvelope(new ErrorBody(code, message)));
}
