using System.Security.Claims;
using System.Text.Encodings.Web;

using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Cluster;
using TheKrystalShip.KGSM.Auth.Users;
using TheKrystalShip.KGSM.Cluster;

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
    MemberActingResolver resolver,
    ApiOptions api)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? handle = Request.Headers[MemberActing.ActingHandleHeader].FirstOrDefault();

        // No handle at all is not this scheme's call to refuse — another one may authenticate it.
        if (string.IsNullOrWhiteSpace(handle))
            return AuthenticateResult.NoResult();

        MemberActingResult result = await resolver.ResolveAsync(
            handle, ClusterRequest.ExtractBearerToken(Request), Context.RequestAborted);

        if (!result.Succeeded)
        {
            // Said at information level with the caller named, because this is what a username collision
            // looks like from the far end: a person who exists in the cluster and resolves to nobody here,
            // with everything else healthy. Every other refusal is ordinary and stays out of the log.
            if (result.Refusal == MemberActingRefusal.NoSuchAccount)
                Logger.LogInformation(
                    "member '{Member}' acted for '{Handle}', which is not an account on this node",
                    result.ActingMember, result.Handle);

            return AuthenticateResult.Fail(result.Failure ?? "the member-acting call was refused");
        }

        KgsmUser person = result.Person!;

        // The tier is the one the resolver read from this node's own replica. It is carried as a claim
        // rather than re-derived here, so the whole scheme has exactly one place authority comes from.
        Claim[] claims =
        [
            new("sub", result.Handle!),
            new(KgsmAuthClaims.Tier, KgsmTiers.ToWire(person.Tier)),
            new(KgsmAuthClaims.Host, api.HostId),
            new(KgsmAuthClaims.TokenKind, KgsmTokenKind.Access),
            new(KgsmAuthClaims.Username, person.Username),
            new(KgsmAuthClaims.Display, person.DisplayName),
            new(MemberActing.ActingMemberClaim, result.ActingMember!),
        ];

        var identity = new ClaimsIdentity(claims, MemberActing.Scheme, "sub", roleType: null);
        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), MemberActing.Scheme));
    }
}
