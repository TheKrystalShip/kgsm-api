using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace TheKrystalShip.Api.Services.Auth;

/// <summary>The claims a caller's access is resolved from on this node.</summary>
public static class AccessClaims
{
    /// <summary>
    /// The account a verified caller resolved to in this node's replica. Absent for a stranger — a
    /// session whose identity no account here holds — who is then refused every action.
    /// </summary>
    public const string Account = "kgsm_account";
}

/// <summary>
/// Gates an endpoint on one action, evaluated for the caller at the target the route names: a server
/// route at that server's install, anything else at this node.
/// </summary>
/// <remarks>
/// The action id is the attribute's argument, which is also what tells the action generator that the
/// engine calls the endpoint makes are checked for that action.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class RequiresActionAttribute(string action) : AuthorizeAttribute(ActionPolicies.Prefix + action)
{
    /// <summary>The action the endpoint performs.</summary>
    public string Action { get; } = action;
}

/// <summary>
/// Marks code below the gate — a service a controller hands the work to — as performing
/// <see cref="Action"/> for a person who was checked for it before the call.
/// </summary>
/// <remarks>
/// Read by the action generator alone, which requires every engine call to be either required by this
/// component's service account or named by the code that makes it for somebody. Nothing reads it at
/// runtime: the check itself is the gate in front.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class PerformedForAttribute(string action) : Attribute
{
    /// <summary>The action the caller was checked for.</summary>
    public string Action { get; } = action;
}

/// <summary>How an action gate is named as an ASP.NET policy.</summary>
public static class ActionPolicies
{
    /// <summary>A policy named <c>action:&lt;id&gt;</c> requires that action.</summary>
    public const string Prefix = "action:";
}

/// <summary>The one requirement an action gate carries.</summary>
public sealed record ActionRequirement(string Action) : IAuthorizationRequirement;

/// <summary>
/// Builds the policy for any <c>action:&lt;id&gt;</c> name on demand, so a new action needs no
/// registration beside the attribute that names it.
/// </summary>
public sealed class ActionPolicyProvider(IOptions<AuthorizationOptions> options) : DefaultAuthorizationPolicyProvider(options)
{
    /// <inheritdoc />
    public override Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (policyName.StartsWith(ActionPolicies.Prefix, StringComparison.Ordinal))
        {
            return Task.FromResult<AuthorizationPolicy?>(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new ActionRequirement(policyName[ActionPolicies.Prefix.Length..]))
                .Build());
        }

        return base.GetPolicyAsync(policyName);
    }
}

/// <summary>
/// Decides an action gate: the caller's account, evaluated from this node's replica at the route's
/// target. An unauthenticated caller fails the policy's first requirement and is challenged (<c>401</c>);
/// one who is refused here is forbidden (<c>403</c>).
/// </summary>
public sealed class ActionAuthorizationHandler(NodeAccess access) : AuthorizationHandler<ActionRequirement>
{
    /// <inheritdoc />
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, ActionRequirement requirement)
    {
        HttpContext? http = context.Resource as HttpContext;
        if (await access.AllowsAsync(context.User, requirement.Action, access.TargetOf(http), http?.RequestAborted ?? default))
            context.Succeed(requirement);
    }
}
