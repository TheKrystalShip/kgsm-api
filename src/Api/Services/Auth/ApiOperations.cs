using Microsoft.AspNetCore.Routing.Patterns;

namespace TheKrystalShip.Api.Services.Auth;

/// <summary>
/// What this node publishes at <c>GET /api/v1/operations</c>: every gated route and the action it
/// requires, so a client gates a control on the request it is about to make and names no action itself.
/// </summary>
/// <remarks>
/// Built from the endpoints this node serves, out of the metadata it enforces with:
/// <see cref="RequiresActionAttribute"/>, which the authorization policy checks, and the
/// <see cref="OperationActionsAttribute"/> kinds, whose entries come from the functions their handlers
/// check with. Every entry matching a request must be held: a settings change carrying the windows needs
/// both the route's action and the windows'.
/// </remarks>
public static class ApiOperations
{
    /// <summary>The prefix every route this document lists is under.</summary>
    public const string Base = "/api/v1";

    public static OperationManifest Build(EndpointDataSource endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var ops = new List<Operation>();

        foreach (RouteEndpoint e in endpoints.Endpoints.OfType<RouteEndpoint>())
        {
            string raw = e.RoutePattern.RawText ?? "";
            string route = OperationManifest.NormalizeRoute(raw);
            if (!route.StartsWith(Base + "/", StringComparison.OrdinalIgnoreCase))
                continue;
            string relative = route[Base.Length..];

            const string scope = Operation.Scopes.Cluster;
            IReadOnlyList<string> methods = e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [];

            foreach (string method in methods)
            {
                foreach (RequiresActionAttribute a in e.Metadata.GetOrderedMetadata<RequiresActionAttribute>())
                    ops.Add(new Operation(method, relative, a.Action, scope));

                foreach (OperationActionsAttribute kind in e.Metadata.GetOrderedMetadata<OperationActionsAttribute>())
                {
                    foreach (OperationAction entry in kind.Entries())
                    {
                        string at = entry.RouteParameter is { } p && entry.RouteValue is { } v
                            ? relative.Replace("{" + p + "}", v, StringComparison.Ordinal)
                            : relative;
                        ops.Add(new Operation(method, at, entry.Action, scope, entry.Field, entry.Value));
                    }
                }
            }
        }

        return OperationManifest.Of(Base, ops);
    }
}
