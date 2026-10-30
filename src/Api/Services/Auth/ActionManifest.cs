using System.Text.Json;
using System.Text.Json.Serialization;

namespace TheKrystalShip.Api.Services.Auth;

/// <summary>
/// One component's action manifest, as its build writes it.
/// </summary>
/// <remarks>
/// The format's authority is <c>kgsm-docs/reference/action-manifest.md</c>. Ids here are local; the wire
/// id is <c>&lt;Component&gt;:&lt;id&gt;</c>.
/// </remarks>
public sealed record ActionManifest(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("component")] string Component,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("actions")] IReadOnlyList<ManifestAction>? Actions,
    [property: JsonPropertyName("requires")] IReadOnlyList<ManifestRequirement>? Requires)
{
    /// <summary>The one schema version this build reads.</summary>
    public const int SupportedSchemaVersion = 1;

    /// <summary>The wire ids of the actions this manifest declares.</summary>
    /// <remarks>An entry with no id or no title is not an action and is left out.</remarks>
    public IEnumerable<string> ActionIds() =>
        (Actions ?? [])
            .Where(a => !string.IsNullOrWhiteSpace(a.Id) && !string.IsNullOrWhiteSpace(a.Title))
            .Select(a => $"{Component}:{a.Id}");
}

/// <summary>One action a manifest declares.</summary>
public sealed record ManifestAction(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("effect")] string? Effect,
    [property: JsonPropertyName("scope")] string? Scope,
    [property: JsonPropertyName("self")] bool? Self);

/// <summary>Another component's action a manifest's component performs as itself.</summary>
public sealed record ManifestRequirement(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("scope")] string? Scope,
    [property: JsonPropertyName("why")] string? Why);

/// <summary>Reads an action manifest from disk.</summary>
public static class ActionManifests
{
    /// <summary>
    /// The manifest at <paramref name="path"/>, or <see langword="null"/> with the reason when it cannot
    /// be read, does not parse, or is a schema version this build does not know.
    /// </summary>
    public static ActionManifest? TryRead(string path, out string? problem)
    {
        problem = null;
        try
        {
            ActionManifest? manifest = JsonSerializer.Deserialize(
                File.ReadAllText(path), AuthJson.Default.ActionManifest);

            if (manifest is null)
                problem = "empty";
            else if (manifest.SchemaVersion != ActionManifest.SupportedSchemaVersion)
                problem = $"schema version {manifest.SchemaVersion}, and this build reads {ActionManifest.SupportedSchemaVersion}";
            else if (string.IsNullOrWhiteSpace(manifest.Component))
                problem = "no component namespace";
            else
                return manifest;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            problem = e.Message;
        }

        return null;
    }
}
