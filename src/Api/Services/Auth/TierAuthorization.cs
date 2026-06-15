using Microsoft.AspNetCore.Authorization;

namespace TheKrystalShip.Api.Services.Auth;

/// <summary>
/// Authorization requirement: the caller's tier (from the <c>tier</c> claim) must be at least
/// <see cref="Minimum"/>. Hierarchical — a <c>viewer</c> requirement is satisfied by operator and
/// admin too (architecture.html §3·f: admin ⊇ operator ⊇ viewer).
/// </summary>
public sealed class TierRequirement(AuthTier minimum) : IAuthorizationRequirement
{
    public AuthTier Minimum { get; } = minimum;
}

/// <summary>
/// Grants a <see cref="TierRequirement"/> when the authenticated principal's <c>tier</c> claim maps
/// to a tier ≥ the requirement. An unauthenticated request fails here too — surfaced as <c>401</c>
/// by the JwtBearer challenge; an authenticated-but-too-low tier is a <c>403</c>. A missing/garbled
/// tier claim parses to <see cref="AuthTier.None"/> and is denied — never a default grant.
/// </summary>
public sealed class TierAuthorizationHandler : AuthorizationHandler<TierRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, TierRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            string? tierClaim = context.User.FindFirst(AuthClaims.Tier)?.Value;
            if (AuthTiers.Parse(tierClaim) >= requirement.Minimum)
                context.Succeed(requirement);
        }
        return Task.CompletedTask;
    }
}
