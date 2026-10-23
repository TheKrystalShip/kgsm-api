using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Notifications;

using ActionIds = TheKrystalShip.Api.Services.Auth.ActionIds;

namespace TheKrystalShip.Api.Services.Notifications;

/// <summary>Where the shared notification routes are mapped, and what gates them.</summary>
public static class NotificationRoutes
{
    /// <summary>The prefix the signed-in routes sit under: a person's own notifications and devices.</summary>
    public const string Prefix = "/api/v1/me";

    /// <summary>
    /// Maps the library's signed-in routes under <see cref="Prefix"/>, gated on <c>api:push.devices</c> —
    /// the self action every active person holds — and its one anonymous route, a button's redemption, at
    /// <see cref="FleetNotifications.ActionsPath"/>. Every route reads and writes only the caller's own rows.
    /// </summary>
    public static IEndpointRouteBuilder MapFleetNotifications(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGroup(Prefix)
            .RequireAuthorization(ActionPolicies.Prefix + ActionIds.PushDevices)
            .MapNotifications(http => http.RequestServices.GetRequiredService<NotificationActors>().PersonOf(http));

        endpoints.MapNotificationActions(FleetNotifications.ActionsPath);
        return endpoints;
    }
}
