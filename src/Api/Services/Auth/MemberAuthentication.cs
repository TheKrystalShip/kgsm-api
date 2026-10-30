using System.Security.Claims;
using System.Text.Encodings.Web;

using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

using TheKrystalShip.KGSM.Cluster;
using TheKrystalShip.KGSM.Cluster.Identity;

namespace TheKrystalShip.Api.Services.Auth;

/// <summary>
/// The scheme names of the one door two kinds of caller come through, and how a request is routed to the
/// one that can answer it.
/// </summary>
/// <remarks>
/// <para>
/// A person presents the access token the sign-in provider issued, signed ES256. Another member of this
/// cluster acting as itself presents the service token its cluster secret signs, HS256. Both ride
/// <c>Authorization: Bearer</c>, and the signing algorithm in the token's header says which it is, so the
/// two are told apart without trying one and falling back to the other: they establish trust in
/// unrelated ways, and a fallback would mean a refused member call quietly re-examined as a person's.
/// </para>
/// <para>
/// A member acting <em>for</em> a person sends that person's token, which is a person's like any other.
/// </para>
/// </remarks>
public static class MemberAuthentication
{
    /// <summary>The scheme that routes each request to the handler that can answer it.</summary>
    public const string Caller = "KgsmCaller";

    /// <summary>The scheme of a member acting as itself.</summary>
    public const string Scheme = "KgsmMember";

    /// <summary>The scheme of a host with no sign-in provider configured: nobody is a person there.</summary>
    public const string NoProvider = "KgsmNoProvider";

    /// <summary>The scheme that answers <paramref name="request"/>.</summary>
    public static string SchemeFor(HttpRequest request, ApiOptions options)
    {
        if (options.AuthDisabled)
            return DisabledAuthHandler.SchemeName;

        if (ClusterRequest.ExtractBearerToken(request) is { } token && IsMemberToken(token))
            return Scheme;

        return options.SessionsConfigured ? CallerId.PersonScheme : NoProvider;
    }

    /// <summary>Whether the token's header names the cluster secret's algorithm.</summary>
    private static bool IsMemberToken(string token)
    {
        try
        {
            return new JsonWebToken(token).Alg == SecurityAlgorithms.HmacSha256;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

/// <summary>
/// Authenticates another member of this cluster acting as itself: its service token is signed with the
/// cluster secret and its member is an enabled one. Such a caller is fully trusted, as the engine's own
/// services are on their host.
/// </summary>
public sealed class MemberAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    IClusterTokenService tokens,
    IClusterMemberGate gate)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    /// <inheritdoc />
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (ClusterRequest.ExtractBearerToken(Request) is not { } token)
            return AuthenticateResult.NoResult();

        ClusterPrincipal? principal = await tokens.ValidateAsync(token).ConfigureAwait(false);
        if (principal is null)
            return AuthenticateResult.Fail("invalid, expired, or unsigned cluster service token");

        if (!await gate.IsEnabledAsync(principal.MemberId).ConfigureAwait(false))
            return AuthenticateResult.Fail($"member '{principal.MemberId}' is not an enabled member of this cluster");

        Claim[] claims = [new("sub", "member:" + principal.MemberId), new("name", principal.MemberId)];
        var identity = new ClaimsIdentity(claims, MemberAuthentication.Scheme, "name", roleType: null);
        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), MemberAuthentication.Scheme));
    }
}

/// <summary>
/// Authenticates nobody, for a host with no sign-in provider configured: the challenge is the ordinary
/// <c>401</c>, and a request that names no action it needs is the only one answered.
/// </summary>
public sealed class NoProviderHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
        Task.FromResult(AuthenticateResult.NoResult());
}
