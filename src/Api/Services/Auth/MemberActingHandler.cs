using System.Security.Claims;
using System.Text.Encodings.Web;

using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

using TheKrystalShip.Auth;
using TheKrystalShip.Auth.Access;
using TheKrystalShip.Auth.Cluster;
using TheKrystalShip.KGSM.Cluster;

namespace TheKrystalShip.Api.Services.Auth;

/// <summary>
/// Another member of this cluster, acting for somebody who is not signed in here — or as one of its own
/// service accounts.
/// </summary>
/// <remarks>
/// <para>
/// The cluster's assistant answers about servers on machines it does not run, and somebody asking it
/// something in Discord holds no session anywhere. So the caller authenticates as a <b>member</b> and
/// names the account it is acting as, and this node decides what that account may do by evaluating it
/// from its own replica.
/// </para>
/// <para>
/// <b>The caller asserts who, never what.</b> What a compromised member could do is act as somebody it
/// names, bounded by what that account actually holds here — and as a service account, only one of its
/// own.
/// </para>
/// <para>
/// <b>An account this node has never heard of is refused, not invented.</b> Provisioning one from an
/// assertion would let any member create accounts here.
/// </para>
/// </remarks>
public sealed class MemberActingHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    MemberActingAccountResolver resolver,
    MemberAccess access,
    ApiOptions api)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    /// <inheritdoc />
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? handle = Request.Headers[MemberActing.ActingHandleHeader].FirstOrDefault();

        // No handle at all is not this scheme's call to refuse — another one may authenticate it.
        if (string.IsNullOrWhiteSpace(handle))
            return AuthenticateResult.NoResult();

        if (await access.EvaluatorAsync(Context.RequestAborted) is not { } evaluator)
        {
            return AuthenticateResult.Fail(new AuthorityUnavailableException(
                access.UnavailableReason ?? "This node's authority replica could not be read."));
        }

        MemberActingAccount result = await resolver.ResolveAsync(
            handle, ClusterRequest.ExtractBearerToken(Request), evaluator.Snapshot, Context.RequestAborted);

        if (!result.Succeeded)
        {
            // Said at information level with the caller named, because this is what a username collision
            // looks like from the far end: a person who exists in the cluster and resolves to nobody here,
            // with everything else healthy. Every other refusal is ordinary and stays out of the log.
            if (result.Refusal == MemberActingRefusal.NoSuchAccount)
            {
                Logger.LogInformation(
                    "member '{Member}' acted for '{Handle}', which is not an account on this node",
                    result.ActingMember, result.Handle);
            }

            return AuthenticateResult.Fail(result.Failure ?? "the member-acting call was refused");
        }

        AccessAccount account = evaluator.Snapshot.Accounts[result.AccountId!];
        Claim[] claims =
        [
            new("sub", result.Handle!),
            new(AccessClaims.Account, result.AccountId!),
            new(KgsmAuthClaims.Host, api.HostId),
            new(KgsmAuthClaims.TokenKind, KgsmTokenKind.Access),
            new(KgsmAuthClaims.Username, account.Name),
            new(KgsmAuthClaims.Display, account.Name),
            new(MemberActing.ActingMemberClaim, result.ActingMember!),
        ];

        var identity = new ClaimsIdentity(claims, MemberActing.Scheme, "sub", roleType: null);
        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), MemberActing.Scheme));
    }
}
