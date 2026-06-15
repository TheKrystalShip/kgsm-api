using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace TheKrystalShip.Api.Services.Auth;

/// <summary>
/// The <c>KGSM_API_AUTH_DISABLED=1</c> escape hatch (the pre-M4 open trust window, now explicit).
/// Authenticates EVERY request as a synthetic <c>admin</c>, so every tier policy passes and the
/// existing smoke/dev flow runs unchanged. Registered as the default scheme only when auth is off;
/// the loud warning is logged once at startup (see <c>Startup.Configure</c>). Never wire this on an
/// exposed host.
/// </summary>
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
        Claim[] claims =
        [
            new("sub", "discord:dev"),
            new(AuthClaims.Tier, AuthTiers.Admin),
            new(AuthClaims.Host, api.HostId),
            new(AuthClaims.TokenKind, TokenKind.Access),
            new(AuthClaims.Username, "dev"),
            new(AuthClaims.Display, "dev (auth disabled)"),
            new("scope", "identify guilds"),
        ];
        var identity = new ClaimsIdentity(claims, SchemeName, "sub", roleType: null);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
