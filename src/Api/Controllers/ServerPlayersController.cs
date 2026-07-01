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
/// The live player roster — <c>GET /servers/{id}/players</c> (player-presence-contract.md §5). Reads the
/// in-memory <see cref="PlayerRosterService"/> projection <see cref="Services.Audit.KgsmAuditConsumer"/>
/// maintains from the <c>player.join</c>/<c>player.leave</c> event stream; this controller itself does no
/// event handling, only the honest <c>configured</c>-vs-<c>unknown</c> gate and the read.
/// </summary>
/// <remarks>
/// <para><b>Gated at operator</b> (the contract's explicit call, not the read-is-viewer default the other
/// server sub-resources use) — a live roster names real connected people/addresses, a step more sensitive
/// than a config value or a backup list.</para>
/// <para><b>Honest-unknown, never a fabricated empty.</b> <see cref="PlayersResponse.Detection"/> is
/// <see cref="PlayerDetection.Unknown"/> when the instance declares NEITHER
/// <c>Instance.PlayerJoinedRegex</c> nor <c>Instance.PlayerLeftRegex</c> — presence is unknowable, not
/// "nobody's here", so <see cref="PlayersResponse.Players"/> is forced to <c>[]</c> in that case
/// regardless of what the (necessarily-untouched) roster projection holds for this id.</para>
/// </remarks>
[ApiController]
[Route("api/v1/servers/{id}/players")]
[Authorize(Policy = AuthPolicy.Operator)]
public sealed class ServerPlayersController(ServerAggregator aggregator, PlayerRosterService roster) : ControllerBase
{
    /// <summary>
    /// The live roster: <c>{ detection: "configured"|"unknown", players: [...] }</c>.
    /// <list type="bullet">
    /// <item><c>404</c> — unknown server id.</item>
    /// <item><c>503</c> — the kgsm engine is not provisioned on this host.</item>
    /// <item><c>200</c> — <c>detection:"unknown"</c> + <c>players:[]</c> when the instance has no
    /// join/left detection configured; otherwise <c>detection:"configured"</c> + the live roster
    /// (possibly empty — a real "nobody's connected").</item>
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

        // The single-instance spawn the detection regexes live on (mirrors ServerConfigController — no
        // second engine call). A null here is the instance vanishing between the roster check and this
        // read: treat as 404, never fabricate a blank/unknown response.
        Instance? instance = instances.GetInstanceInfo(id);
        if (instance is null)
            return NotFound();

        // "unknown" iff NEITHER regex is set — presence is unknowable, not zero. Only one configured is
        // still "configured" (a game may only need one side, e.g. a join-only heartbeat) — the contract's
        // gate is "no detection at all", not "incomplete detection".
        bool unknown = string.IsNullOrEmpty(instance.PlayerJoinedRegex) && string.IsNullOrEmpty(instance.PlayerLeftRegex);
        if (unknown)
            return Ok(new PlayersResponse(PlayerDetection.Unknown, []));

        return Ok(new PlayersResponse(PlayerDetection.Configured, roster.GetRoster(id)));
    }

    // The honest 404 source (the roster is the authority) — the same convention as every other
    // servers/{id}/* sub-resource controller.
    private async Task<bool> ExistsAsync(string id, CancellationToken ct)
    {
        IReadOnlyList<Server> servers = await aggregator.GetServersAsync(ct).ConfigureAwait(false);
        return servers.Any(s => string.Equals(s.Id, id, StringComparison.Ordinal));
    }

    private ObjectResult Error(int statusCode, string code, string message) =>
        StatusCode(statusCode, new ErrorEnvelope(new ErrorBody(code, message)));
}
