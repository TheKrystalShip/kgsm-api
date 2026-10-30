using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.Extensions.Options;

using TheKrystalShip.Api.Infrastructure;
using TheKrystalShip.KGSM.Sessions;

namespace TheKrystalShip.Api.Services.Auth;

/// <summary>
/// Gates an endpoint on one action: the caller's token must list it. Actions are held cluster-wide, so
/// the route's target does not narrow the check.
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

/// <summary>
/// Builds the policy for any <c>action:&lt;id&gt;</c> name on demand, so a new action needs no
/// registration beside the attribute that names it. The policy is the sessions package's requirement,
/// authenticated through the caller scheme so a person, a member and an open host are each admitted.
/// </summary>
public sealed class ActionPolicyProvider(IOptions<AuthorizationOptions> options) : DefaultAuthorizationPolicyProvider(options)
{
    /// <inheritdoc />
    public override Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (policyName.StartsWith(ActionPolicies.Prefix, StringComparison.Ordinal))
        {
            return Task.FromResult<AuthorizationPolicy?>(new AuthorizationPolicyBuilder(MemberAuthentication.Caller)
                .AddRequirements(new ActionRequirement([policyName[ActionPolicies.Prefix.Length..]]))
                .Build());
        }

        return base.GetPolicyAsync(policyName);
    }
}

/// <summary>
/// Admits the callers that hold every action without a token listing them: another member acting as
/// itself, and the open caller of a host with authentication disabled. A person is admitted by the
/// sessions package's own handler, from the actions their token lists.
/// </summary>
public sealed class TrustedCallerHandler : AuthorizationHandler<ActionRequirement>
{
    /// <inheritdoc />
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, ActionRequirement requirement)
    {
        if (CallerId.Of(context.User) is { Kind: CallerKind.Member or CallerKind.Open })
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Writes a refused action gate as the <c>403</c> every member answers: the error envelope, saying which
/// action the request needs. Every other outcome is the framework's.
/// </summary>
public sealed class ActionRefusalResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    /// <inheritdoc />
    public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        string? action = authorizeResult.Forbidden
            ? policy.Requirements.OfType<ActionRequirement>().FirstOrDefault()?.AnyOf.FirstOrDefault()
            : null;
        return action is null
            ? _default.HandleAsync(next, context, policy, authorizeResult)
            : ApiErrors.WriteAsync(context, StatusCodes.Status403Forbidden, "forbidden", $"This needs {action}.");
    }
}
