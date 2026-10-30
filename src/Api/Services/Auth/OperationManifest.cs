using System.Text.Json.Serialization;

namespace TheKrystalShip.Api.Services.Auth;

/// <summary>
/// The operations this node serves, and the action each one requires: what a client gates a control on.
/// </summary>
/// <remarks>
/// A client names no action. It knows the request it is about to make, and asks this node which action
/// that request needs. The document is built from the same metadata the node enforces with, so nothing is
/// enforced that is not published here. Whether the caller holds the action is <c>GET /me/access</c>.
/// </remarks>
/// <param name="SchemaVersion">The shape's version. A client that does not know it gates nothing open.</param>
/// <param name="Base">The path prefix every route is under on this node (<c>/api/v1</c>), or empty.</param>
/// <param name="Operations">Every gated operation, in route order.</param>
public sealed record OperationManifest(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("base")] string Base,
    [property: JsonPropertyName("operations")] IReadOnlyList<Operation> Operations)
{
    /// <summary>The only schema version this shape is.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// The document for <paramref name="operations"/>: methods upper-cased, routes with one leading slash
    /// and none trailing, duplicates dropped, sorted so two builds serve identical bytes.
    /// </summary>
    public static OperationManifest Of(string basePath, IEnumerable<Operation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        Operation[] normalized = [.. operations
            .Select(o => o with { Method = o.Method.ToUpperInvariant(), Route = NormalizeRoute(o.Route) })
            .Distinct()
            .OrderBy(o => o.Route, StringComparer.Ordinal)
            .ThenBy(o => o.Method, StringComparer.Ordinal)
            .ThenBy(o => o.Value ?? "", StringComparer.Ordinal)];
        string prefix = string.IsNullOrWhiteSpace(basePath) ? "" : NormalizeRoute(basePath);
        return new OperationManifest(CurrentSchemaVersion, prefix, normalized);
    }

    /// <summary>A route with exactly one leading slash and no trailing one.</summary>
    public static string NormalizeRoute(string route)
    {
        ArgumentNullException.ThrowIfNull(route);
        string trimmed = route.Trim().Trim('/');
        return trimmed.Length == 0 ? "/" : "/" + trimmed;
    }
}

/// <summary>One request this node serves, and what it takes to make it.</summary>
/// <param name="Method">The HTTP method, upper-case.</param>
/// <param name="Route">
/// The route template under the document's base, with parameters in braces (<c>/servers/{id}/backups</c>).
/// A literal segment is more specific than a parameter.
/// </param>
/// <param name="Action">
/// The action the operation requires. It may name a route parameter in braces, such as
/// <c>{leaf}:config.write</c>, which the client fills from the path it matched.
/// </param>
/// <param name="Scope">Where the action is held: always <see cref="Scopes.Cluster"/>.</param>
/// <param name="Field">
/// For a route that performs several actions, the body field that says which, such as a lifecycle
/// command's <c>verb</c>. Null when the route is one action.
/// </param>
/// <param name="Value">The value of <see cref="Field"/> this entry answers for.</param>
public sealed record Operation(
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("route")] string Route,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("field")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Field = null,
    [property: JsonPropertyName("value")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Value = null)
{
    /// <summary>The scope words an operation is held at.</summary>
    public static class Scopes
    {
        /// <summary>Held cluster-wide, the only scope there is.</summary>
        public const string Cluster = "cluster";
    }
}
