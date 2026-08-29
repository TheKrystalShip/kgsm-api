using System.Text.Json;
using TheKrystalShip.Api.Services.Auth;

using TheKrystalShip.KGSM.Auth.Sessions;
using TheKrystalShip.KGSM.Cluster.Messaging;

namespace TheKrystalShip.Api.Services.Cluster.Handlers;

/// <summary>
/// The cluster bus's first message type (<c>docs/cluster-message-bus-plan.md §3</c>, the "sign out
/// everywhere" customer the bus was built for). Payload: <c>{ scope: "user"|"sid"|"all",
/// discordId?, sid? }</c>. Delegates the actual revoke to the SAME <see cref="SessionStore"/> the
/// local <c>/auth/session/revoke</c> endpoints use — this handler is just the cluster-bus front door
/// onto that existing authority, so a member-triggered revoke and a local one are indistinguishable
/// once applied.
/// </summary>
/// <remarks>
/// <b>Idempotent by construction.</b> <see cref="SessionStore.RevokeAsync"/> and
/// <see cref="SessionStore.RevokeAllForUserAsync"/> are themselves no-ops on an already-revoked/absent
/// row, so replaying this handler for the same envelope (or receiving two different envelopes that
/// both target the same session) is always harmless — exactly what <see cref="IClusterMessageHandler"/>
/// requires. A malformed-but-known-type payload (missing the field the scope needs) is logged and
/// swallowed, never thrown — throwing here surfaces as a transient <c>500</c> and the sender retries
/// forever against a payload that can never become valid.
/// </remarks>
public sealed class SessionRevokeHandler(
    SessionStore sessionStore,
    ISessionValidator sessionValidator,
    ApiOptions options,
    ILogger<SessionRevokeHandler> logger) : IClusterMessageHandler
{
    public string Type => "session.revoke";

    public async Task HandleAsync(ClusterEnvelope envelope, CancellationToken ct)
    {
        string? scope = GetString(envelope.Payload, "scope");
        switch (scope)
        {
            case "sid":
            {
                string? sid = GetString(envelope.Payload, "sid");
                if (string.IsNullOrWhiteSpace(sid))
                {
                    logger.LogWarning(
                        "cluster session.revoke (id={Id} from={From}): scope=sid but no sid in payload — no-op",
                        envelope.Id, envelope.From);
                    return;
                }
                // Idempotent: RevokeAsync no-ops (returns false) on an already-revoked or absent row.
                await sessionStore.RevokeAsync(sid, ct).ConfigureAwait(false);
                sessionValidator.Evict(sid);
                break;
            }

            case "user":
            {
                string? discordId = GetString(envelope.Payload, "discordId");
                if (string.IsNullOrWhiteSpace(discordId))
                {
                    logger.LogWarning(
                        "cluster session.revoke (id={Id} from={From}): scope=user but no discordId in payload — no-op",
                        envelope.Id, envelope.From);
                    return;
                }
                string userId = $"discord:{discordId}";
                IReadOnlyList<string> revokedSids =
                    await sessionStore.RevokeAllForUserAsync(userId, options.HostId, ct).ConfigureAwait(false);
                foreach (string sid in revokedSids)
                    sessionValidator.Evict(sid);
                break;
            }

            case "all":
                // Reserved. A host-wide revoke-everything is not implemented, so log loudly and no-op
                // rather than guess at a destructive interpretation of it.
                logger.LogWarning(
                    "cluster session.revoke (id={Id} from={From}): scope=all is reserved and not " +
                    "implemented — no-op",
                    envelope.Id, envelope.From);
                break;

            default:
                logger.LogWarning(
                    "cluster session.revoke (id={Id} from={From}): unrecognized/missing scope {Scope} — no-op",
                    envelope.Id, envelope.From, scope);
                break;
        }
    }

    private static string? GetString(JsonElement payload, string propertyName) =>
        payload.ValueKind == JsonValueKind.Object
        && payload.TryGetProperty(propertyName, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
