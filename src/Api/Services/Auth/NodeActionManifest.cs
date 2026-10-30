using System.Reflection;
using System.Text.Json.Serialization;

using TheKrystalShip.Auth.Access;

namespace TheKrystalShip.Api.Services.Auth;

/// <summary>
/// The actions this node's components perform, as the document the provider pulls to learn what the
/// cluster's application can grant.
/// </summary>
/// <param name="SchemaVersion">The action-manifest schema version of every manifest in it.</param>
/// <param name="Version">This API's build version.</param>
/// <param name="Manifests">
/// One manifest per component on this node, each in its own namespace, in the application format: every
/// action at the one scope an application has, no requirements.
/// </param>
public sealed record NodeActionManifest(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("manifests")] IReadOnlyList<ActionManifest> Manifests)
{
    /// <summary>The route the document is served at, anonymously.</summary>
    public const string Path = "/.well-known/tks-actions.json";

    /// <summary>
    /// The document for the manifests <paramref name="installed"/> on this node. The provider's own
    /// <c>auth:*</c> actions are the provider's, not this cluster's, and are left out.
    /// </summary>
    public static NodeActionManifest From(IEnumerable<ActionManifest> installed) => new(
        ActionManifest.SupportedSchemaVersion,
        typeof(NodeActionManifest).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] ?? "0.0.0",
        [.. installed
            .Where(m => m.Component != global::TheKrystalShip.Auth.Access.ActionIds.AuthComponent)
            .OrderBy(m => m.Component, StringComparer.Ordinal)
            .Select(m => m with
            {
                Requires = [],
                Actions = [.. (m.Actions ?? []).OrderBy(a => a.Id, StringComparer.Ordinal)
                    .Select(a => a with { Scope = "cluster", Self = a.Self is true ? true : null })],
            })]);
}

/// <summary>Source-generated JSON for <see cref="NodeActionManifest"/>.</summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(NodeActionManifest))]
public sealed partial class NodeActionManifestJson : JsonSerializerContext;
