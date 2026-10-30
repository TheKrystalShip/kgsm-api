using System.Text.Json.Serialization;

namespace TheKrystalShip.Api.Services.Auth;

/// <summary>
/// The document naming this node's sign-in provider (RFC 9728), served at <see cref="Path"/>.
/// </summary>
/// <param name="Resource">The origin the document was asked for at.</param>
/// <param name="AuthorizationServers">The provider's issuer.</param>
/// <param name="BearerMethodsSupported">How a token is presented: in the header.</param>
public sealed record ProtectedResourceDocument(
    [property: JsonPropertyName("resource")] string Resource,
    [property: JsonPropertyName("authorization_servers")] IReadOnlyList<string> AuthorizationServers,
    [property: JsonPropertyName("bearer_methods_supported")] IReadOnlyList<string> BearerMethodsSupported)
{
    /// <summary>Where the document is served.</summary>
    public const string Path = "/.well-known/oauth-protected-resource";

    /// <summary>The answer while no issuer is configured.</summary>
    public const string NoIssuer =
        """{"error":"no_issuer","error_description":"This node knows of no sign-in provider yet."}""";

    /// <summary>The document naming <paramref name="issuer"/> for <paramref name="resource"/>.</summary>
    public static ProtectedResourceDocument For(string resource, string issuer) => new(resource, [issuer], ["header"]);

    /// <summary>Whether <paramref name="issuer"/> is an address a browser can be sent to sign in at.</summary>
    public static bool IsProvider(string? issuer) =>
        Uri.TryCreate(issuer, UriKind.Absolute, out Uri? uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);
}
