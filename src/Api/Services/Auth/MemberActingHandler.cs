using System.Security.Claims;
using System.Text.Encodings.Web;

using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Cluster;
using TheKrystalShip.KGSM.Auth.Users;
using TheKrystalShip.KGSM.Cluster.Identity;

namespace TheKrystalShip.Api.Services.Auth;

/// <summary>
/// Another member of this cluster, acting for somebody who is not signed in here.
/// </summary>
/// <remarks>
/// <para>
/// The cluster's assistant answers about servers on machines it does not run, and somebody asking it
/// something in Discord holds no session anywhere. So the caller authenticates as a <b>member</b> and
/// names the person it is acting for, and this node decides what that person may do by reading its own
/// replica of the cluster's accounts.
/// </para>
/// <para>
/// <b>The caller asserts who, never what.</b> No tier crosses the wire in either direction. What a
/// compromised member could do is act as somebody it names, bounded by what that person actually
/// holds — which is narrower than a shared secret that forwards an authority along with an identity,
/// and it is the same boundary every member-to-member call in this cluster already sits on.
/// </para>
/// <para>
/// <b>A person this node has never heard of is refused, not invented.</b> The handle is resolved
/// against the replica, and an account that is not there means the caller is naming somebody this node
/// cannot answer for. Provisioning one from an assertion would let any member create accounts here.
/// </para>
/// </remarks>
public sealed class MemberActingHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    IClusterTokenService tokens,
    IClusterMemberGate members,
    UserDirectory users,
    ApiOptions api)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? handle = Request.Headers[MemberActing.ActingHandleHeader].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(handle))
            return AuthenticateResult.NoResult();

        string? presented = Bearer();
        if (presented is null)
            return AuthenticateResult.Fail("a member-acting call must present a member service token");

        ClusterPrincipal? caller = await tokens.ValidateAsync(presented);
        if (caller is null || string.IsNullOrEmpty(caller.MemberId))
            return AuthenticateResult.Fail("the member service token is not valid here");

        // The disable-list, which is the one local override to the shared-secret trust boundary. A
        // member somebody has switched off must not act for anybody, however good its token is.
        if (!await members.IsEnabledAsync(caller.MemberId))
            return AuthenticateResult.Fail($"member '{caller.MemberId}' is disabled here");

        if (!KgsmActor.TryParse(handle, out _, out _))
            return AuthenticateResult.Fail("the acting handle is not a 'provider:subject' handle");

        if (!users.Available)
            return AuthenticateResult.Fail("this node's account store is unavailable");

        KgsmUser? person;
        try
        {
            person = await users.Store.FindByCredentialAsync(handle, Context.RequestAborted);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "could not resolve '{Handle}' against this node's accounts", handle);
            return AuthenticateResult.Fail("this node's account store could not be read");
        }

        if (person is null)
        {
            // Said at information level with the caller named, because this is what a username
            // collision looks like from the far end: a person who exists in the cluster and resolves
            // to nobody here, with everything else healthy.
            Logger.LogInformation(
                "member '{Member}' acted for '{Handle}', which is not an account on this node",
                caller.MemberId, handle);
            return AuthenticateResult.Fail("no account here matches the acting handle");
        }

        if (person.Status == UserStatus.Disabled)
            return AuthenticateResult.Fail("that account is disabled here");

        // The tier, from this node's own replica and nowhere else. It is read here rather than left to
        // LiveAuthority because that runs on the session path; this is the same rule applied at the
        // only point this scheme has.
        Claim[] claims =
        [
            new("sub", handle),
            new(KgsmAuthClaims.Tier, KgsmTiers.ToWire(person.Tier)),
            new(KgsmAuthClaims.Host, api.HostId),
            new(KgsmAuthClaims.TokenKind, KgsmTokenKind.Access),
            new(KgsmAuthClaims.Username, person.Username),
            new(KgsmAuthClaims.Display, person.DisplayName),
            new(MemberActing.ActingMemberClaim, caller.MemberId),
        ];

        var identity = new ClaimsIdentity(claims, MemberActing.Scheme, "sub", roleType: null);
        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), MemberActing.Scheme));
    }

    private string? Bearer()
    {
        string header = Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : null;
    }
}
