using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Aggregation;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Players;
using TheKrystalShip.Api.Data;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;

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
/// <see cref="PlayerDetection.Unknown"/> when this host cannot observe the instance's players at all
/// — presence is unknowable, not "nobody's here", so <see cref="PlayersResponse.Players"/> is forced
/// to <c>[]</c> in that case regardless of what the history projection holds for this id.</para>
/// <para><b>The supervisor answers that, not this controller.</b> Detection is read through
/// <see cref="PlayerObservability"/> from <c>IWatchdogClient.GetPlayerPresenceAsync</c> — the same
/// predicate the ingesters gate on, and the same reading the online count carried on every server
/// element is built from, so the two can never disagree. It is the predicate that
/// accounts for RCON polling as well as log matching and for whether a pattern actually
/// <i>compiles</i>. Deriving it from the instance's regex fields instead under-reports every
/// RCON-polled game as unknowable while the supervisor is actively reading its roster, and it is
/// not a derivation a consumer can get right in any case.</para>
/// <para><b>An unreachable supervisor is <see cref="PlayerDetection.Unknown"/>, never configured.</b>
/// Not knowing whether presence is observable is the same refusal as knowing it is not: in both cases
/// this host cannot stand behind a roster.</para>
/// </remarks>
[ApiController]
[Route("api/v1/servers/{id}/players")]
[Authorize(Policy = AuthPolicy.Operator)]
public sealed class ServerPlayersController(
    ServerAggregator aggregator,
    PlayerHistoryService history,
    PlayerObservability observability) : ControllerBase
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

        // Moderation is reported on both branches: what a game CAN do is declared by its blueprint and
        // is true whether or not this host can see who is connected. Suppressing it on `unknown` would
        // conflate "we cannot see the players" with "this game cannot be moderated".
        ModerationCapability moderation = ModerationTargetResolver.Describe(instance);

        if (!await IsObservableAsync(id, ct).ConfigureAwait(false))
            return Ok(new PlayersResponse(PlayerDetection.Unknown, [], moderation));

        return Ok(new PlayersResponse(PlayerDetection.Configured, history.GetRoster(id), moderation));
    }

    /// <summary>
    /// Disconnect a player. The action names a roster entry, never an address — see
    /// <see cref="ModerateAsync"/> for why.
    /// </summary>
    [HttpPost("{playerIdentity}/kick")]
    public Task<IActionResult> Kick(string id, string playerIdentity, [FromQuery] string? origin, CancellationToken ct)
        => ModerateAsync(id, playerIdentity, ModerationAction.Kick, origin, ct);

    /// <summary>Disconnect a player and block them from reconnecting.</summary>
    [HttpPost("{playerIdentity}/ban")]
    public Task<IActionResult> Ban(string id, string playerIdentity, [FromQuery] string? origin, CancellationToken ct)
        => ModerateAsync(id, playerIdentity, ModerationAction.Ban, origin, ct);

    /// <summary>
    /// Lift a block. The selectable set is this server's roster entries with
    /// <see cref="PlayerStatus.banned"/> — an unban's subject is by definition not connected, so it
    /// cannot come from a live presence view, and it is scoped to this server because a ban is.
    /// </summary>
    [HttpPost("{playerIdentity}/unban")]
    public Task<IActionResult> Unban(string id, string playerIdentity, [FromQuery] string? origin, CancellationToken ct)
        => ModerateAsync(id, playerIdentity, ModerationAction.Unban, origin, ct);

    /// <summary>
    /// The one path all three actions take, so the target can only ever be resolved one way.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The caller does not name the target.</b> It names a roster entry by the roster's own opaque
    /// key; every identity field that reaches the game comes from the record that key resolves to,
    /// scoped to this server. A request carrying its own address or name would let any caller ban an
    /// arbitrary address the roster never saw.
    /// </para>
    /// <para>
    /// <b>The game decides which identity.</b> The blueprint's template declares it through its
    /// placeholder, read by <see cref="ModerationTargetResolver"/>. A game that declares no command
    /// for the action is refused (<c>409</c>) rather than sent a different one, and a player missing
    /// the identity the game asks for is refused too — never substituted with whichever field happens
    /// to be present.
    /// </para>
    /// <para>
    /// <b>No audit row is written here.</b> kgsm emits <c>instance_player_kicked</c>/<c>_banned</c>/
    /// <c>_unbanned</c> and the M5 consumer writes the row from that echo; this path stamps
    /// <c>actor</c>+<c>origin</c> so the echo carries who did it. Writing one here too would double it.
    /// </para>
    /// <list type="bullet">
    /// <item><c>400</c> — bad origin.</item>
    /// <item><c>404</c> — unknown server, or a player this server has never seen.</item>
    /// <item><c>409</c> — the game declares no command for this action, or the player carries no
    /// identity of the kind it asks for.</item>
    /// <item><c>502</c> — the engine refused the command (e.g. the server is not running).</item>
    /// <item><c>503</c> — the kgsm engine is not provisioned on this host.</item>
    /// <item><c>200</c> — <c>{ serverId, playerIdentity, action, targetKind }</c>.</item>
    /// </list>
    /// </remarks>
    private async Task<IActionResult> ModerateAsync(
        string id, string playerIdentity, string action, string? rawOrigin, CancellationToken ct)
    {
        if (!TryResolveOrigin(rawOrigin, out string origin))
            return Error(StatusCodes.Status400BadRequest, "bad_request",
                "unknown origin; expected one of: ui, assistant, discord, api");

        if (HttpContext.RequestServices.GetService(typeof(IInstanceService)) is not IInstanceService instances)
            return Error(StatusCodes.Status503ServiceUnavailable, "unavailable",
                "the kgsm engine is not provisioned on this host");

        if (!await ExistsAsync(id, ct).ConfigureAwait(false))
            return NotFound();

        Instance? instance = instances.GetInstanceInfo(id);
        if (instance is null)
            return NotFound();

        if (!history.TryGetPlayer(id, playerIdentity, out RosterPlayer player))
            return Error(StatusCodes.Status404NotFound, "not_found",
                $"no player '{playerIdentity}' has been seen on this server");

        string? template = action switch
        {
            ModerationAction.Kick => instance.KickCommand,
            ModerationAction.Ban => instance.BanCommand,
            _ => instance.UnbanCommand
        };

        ModerationTargetResolver.Failure failure =
            ModerationTargetResolver.TryResolve(template, player, out string target, out ModerationTargetKind kind);

        if (failure == ModerationTargetResolver.Failure.Unsupported)
            return Error(StatusCodes.Status409Conflict, "conflict",
                $"this game declares no {action} command");

        if (failure == ModerationTargetResolver.Failure.NoSuchIdentity)
            return Error(StatusCodes.Status409Conflict, "conflict",
                $"this game moderates by {kind.ToString().ToLowerInvariant()}, which this player has none of");

        // actor = the bearer identity; it rides onto the kgsm event so the audit row the consumer
        // writes from the echo names who caused it.
        string? actor = AuditPrincipal.ActorString(User);

        KgsmResult result = action switch
        {
            ModerationAction.Kick => instances.Kick(id, target, actor, origin),
            ModerationAction.Ban => instances.Ban(id, target, actor, origin),
            _ => instances.Unban(id, target, actor, origin)
        };

        if (!result.IsSuccess)
            return Error(StatusCodes.Status502BadGateway, "engine_error",
                string.IsNullOrWhiteSpace(result.Stderr)
                    ? $"the engine refused the {action}"
                    : result.Stderr.Trim());

        return Ok(new ModerationResult(id, playerIdentity, action, kind.ToString().ToLowerInvariant()));
    }

    private static bool TryResolveOrigin(string? raw, out string origin)
    {
        origin = raw?.Trim().ToLowerInvariant() is { Length: > 0 } o ? o : AuditOrigin.Api;
        return AuditOrigin.IsCallerDeclarable(origin);
    }

    private async Task<bool> ExistsAsync(string id, CancellationToken ct)
    {
        IReadOnlyList<Server> servers = await aggregator.GetServersAsync(ct).ConfigureAwait(false);
        return servers.Any(s => string.Equals(s.Id, id, StringComparison.Ordinal));
    }

    /// <summary>
    /// Whether this host can observe who is connected to <paramref name="id"/> — the supervisor's
    /// answer, not a second opinion derived from the same instance config.
    /// </summary>
    /// <remarks>
    /// <b>Every "no" arrives here as false, and they are deliberately not distinguished.</b> The
    /// supervisor not being provisioned, not being reachable, not knowing this instance, and knowing
    /// it reports nothing are four different reasons for one answer: this host cannot stand behind a
    /// roster. Reporting any of them as <c>configured</c> would put a roster the API cannot vouch for
    /// in front of an operator.
    /// </remarks>
    // Detection comes from PlayerObservability — the one cached reading of the supervisor's answer that
    // the online count on every server element is also built from, so this endpoint's `detection` and that
    // count cannot contradict each other. An unreachable or absent supervisor reads as unobservable there
    // exactly as it did here.
    private async Task<bool> IsObservableAsync(string id, CancellationToken ct)
    {
        await observability.RefreshIfStaleAsync(ct).ConfigureAwait(false);
        return observability.IsObservable(id);
    }

    private ObjectResult Error(int statusCode, string code, string message) =>
        StatusCode(statusCode, new ErrorEnvelope(new ErrorBody(code, message)));
}
