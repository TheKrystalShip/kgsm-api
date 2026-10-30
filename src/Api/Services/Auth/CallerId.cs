using System.Security.Claims;

using TheKrystalShip.KGSM.Cluster;

namespace TheKrystalShip.Api.Services.Auth;

/// <summary>What stands behind a request.</summary>
public enum CallerKind
{
    /// <summary>A person, admitted on the access token the sign-in provider issued them.</summary>
    Person,

    /// <summary>Another member of the cluster acting as itself, admitted on the cluster secret.</summary>
    Member,

    /// <summary>Anybody, on a host run with authentication disabled.</summary>
    Open,
}

/// <summary>
/// Who a request is made by: the stable subject records are keyed by, the name a person reads, and the
/// audit actor the engine echoes.
/// </summary>
/// <param name="Kind">What stands behind the request.</param>
/// <param name="Subject">The token's <c>sub</c> for a person; <c>member:&lt;id&gt;</c> for a member.</param>
/// <param name="DisplayName">The token's <c>name</c> for a person, stored beside the subject wherever a record keeps one.</param>
public sealed record CallerId(CallerKind Kind, string Subject, string DisplayName)
{
    /// <summary>The scheme of a person's bearer.</summary>
    public const string PersonScheme = "Bearer";

    /// <summary>
    /// The access token <paramref name="http"/> presented when a person made it, or <see langword="null"/>.
    /// It is what a request made on a person's behalf to a leaf or to another member carries onward, so the
    /// receiver admits the person on their own token.
    /// </summary>
    public static string? PersonToken(HttpContext http) =>
        Of(http.User) is { Kind: CallerKind.Person } ? ClusterRequest.ExtractBearerToken(http.Request) : null;

    /// <summary>
    /// The author an automation saved by this caller is recorded under: the subject of a person, and
    /// <see langword="null"/> for any other caller, which has no account to be the author of anything.
    /// </summary>
    public static string? AuthorOf(ClaimsPrincipal user) => Of(user) is { Kind: CallerKind.Person } c ? c.Subject : null;

    /// <summary>The audit actor: <c>local:&lt;name&gt;</c> for a person, <c>system:&lt;member&gt;</c> for a member.</summary>
    public string Actor => Kind switch
    {
        CallerKind.Member => $"system:{DisplayName}",
        CallerKind.Person => $"local:{DisplayName}",
        _ => Subject,
    };

    /// <summary>
    /// The caller behind <paramref name="user"/>, or <see langword="null"/> for an unauthenticated one.
    /// </summary>
    /// <remarks>
    /// The actor of the open caller is the configured one, carried whole in its <c>sub</c>.
    /// </remarks>
    public static CallerId? Of(ClaimsPrincipal? user)
    {
        if (user?.Identity is not ClaimsIdentity { IsAuthenticated: true } identity)
            return null;

        string? subject = identity.FindFirst("sub")?.Value;
        if (string.IsNullOrEmpty(subject))
            return null;

        string name = identity.FindFirst("name")?.Value is { Length: > 0 } n ? n : subject;
        return identity.AuthenticationType switch
        {
            MemberAuthentication.Scheme => new CallerId(CallerKind.Member, subject, name),
            DisabledAuthHandler.SchemeName => new CallerId(CallerKind.Open, subject, name),
            PersonScheme => new CallerId(CallerKind.Person, subject, name),
            _ => null,
        };
    }
}
