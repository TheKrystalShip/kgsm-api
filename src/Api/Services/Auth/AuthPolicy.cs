namespace TheKrystalShip.Api.Services.Auth;

/// <summary>
/// This API's authorization policy names — one per tier, hierarchical, so a viewer policy admits an
/// operator and an admin too (see <c>TierAuthorizationHandler</c>).
/// </summary>
/// <remarks>
/// Policy names stay here rather than moving to the shared auth package: they are ASP.NET's
/// vocabulary for wiring an endpoint to a requirement, not part of the ecosystem's authorization
/// model. The tiers themselves are shared; how this one surface enforces them is its own business.
/// </remarks>
public static class AuthPolicy
{
    public const string Viewer = "viewer";
    public const string Operator = "operator";
    public const string Admin = "admin";
}
