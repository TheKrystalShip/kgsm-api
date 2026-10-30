using System.Text.Json.Serialization;

namespace TheKrystalShip.Api.Services.Auth;

/// <summary>
/// What <c>GET /me/access</c> answers: the actions the caller's token lists that this node performs.
/// </summary>
/// <remarks>
/// A client gates a control by looking an action up in <see cref="Cluster"/>. A grant is cluster-wide, so
/// <see cref="Nodes"/> and <see cref="Instances"/> are always empty; they stay in the shape so a client
/// that reads them finds nothing narrower than the cluster.
/// </remarks>
/// <param name="Version">Always 0: there is no authority version on a node that holds no copy of one.</param>
/// <param name="Current">Always true: the answer is the token as presented.</param>
/// <param name="Cluster">The actions the token lists, among those this node's components perform.</param>
/// <param name="Nodes">Always empty.</param>
/// <param name="Instances">Always empty.</param>
/// <param name="Owner">
/// Whether the caller holds every action, including ones no manifest declares: only the open caller of an
/// auth-disabled host. A token lists actions, never "everything".
/// </param>
public sealed record AccessReport(
    [property: JsonPropertyName("version")] long Version,
    [property: JsonPropertyName("current")] bool Current,
    [property: JsonPropertyName("cluster")] IReadOnlyList<string> Cluster,
    [property: JsonPropertyName("nodes")] IReadOnlyDictionary<string, IReadOnlyList<string>> Nodes,
    [property: JsonPropertyName("instances")] IReadOnlyDictionary<string, IReadOnlyList<string>> Instances,
    [property: JsonPropertyName("owner")] bool Owner = false)
{
    /// <summary>The report for a caller holding <paramref name="actions"/>.</summary>
    public static AccessReport Of(IReadOnlyList<string> actions, bool owner = false) =>
        new(0, Current: true, actions,
            new Dictionary<string, IReadOnlyList<string>>(), new Dictionary<string, IReadOnlyList<string>>(), owner);
}

/// <summary>Source-generated JSON for the documents this node's access model writes and reads.</summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ActionManifest))]
[JsonSerializable(typeof(OperationManifest))]
[JsonSerializable(typeof(AccessReport))]
[JsonSerializable(typeof(NodeActionManifest))]
[JsonSerializable(typeof(ProtectedResourceDocument))]
internal sealed partial class AuthJson : JsonSerializerContext;
