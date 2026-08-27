using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

using TheKrystalShip.KGSM.Auth;

namespace TheKrystalShip.Api.Services.Auth;

/// <summary>
/// The <c>Api__AuthDisabled=true</c> escape hatch. Authenticates EVERY request as a synthetic
/// <c>admin</c>, so every tier policy passes and the smoke/dev flow runs with no login. Registered as
/// the default scheme only when auth is off; the loud warning is logged once at startup (see
/// <c>Startup.Configure</c>). Never wire this on an exposed host.
/// </summary>
/// <remarks>
/// <para>
/// The identity it presents is <see cref="ApiOptions.DisabledAuthActor"/> — configuration, with no
/// default, refused at startup unless it is a well-formed <c>provider:name</c>. An open door still
/// writes audit rows, and whoever runs the host is the only one who can say whose name belongs on
/// them; a name compiled in here would be a fabricated principal, and one that claims a provider it
/// never authenticated against is worse still — it is indistinguishable in the record from a real
/// person who logged in.
/// </para>
/// </remarks>
public sealed class DisabledAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    ApiOptions api)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    public const string SchemeName = "Disabled";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Startup refuses to build this handler's host without a parseable actor, so the split holds.
        // The provider half rides on `sub`, which is where every reader takes it from.
        KgsmActor.TryParse(api.DisabledAuthActor, out _, out string name);

        Claim[] claims =
        [
            new("sub", api.DisabledAuthActor),
            new(KgsmAuthClaims.Tier, KgsmTiers.Admin),
            new(KgsmAuthClaims.Host, api.HostId),
            new(KgsmAuthClaims.TokenKind, KgsmTokenKind.Access),
            new(KgsmAuthClaims.Username, name),
            new(KgsmAuthClaims.Display, name),
            new("scope", "identify guilds"),
        ];
        var identity = new ClaimsIdentity(claims, SchemeName, "sub", roleType: null);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
