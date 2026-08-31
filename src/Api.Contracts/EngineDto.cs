using System.Text.Json.Serialization;

namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// The kgsm engine's identity card — <c>GET /hosts/{id}/engine</c>, the Overview of the engine's
/// pseudo-leaf page. Everything here is read from the engine itself (<c>kgsm --version</c> /
/// <c>kgsm --paths --json</c> through kgsm-lib) except <see cref="Path"/>, which is this API's own
/// configuration — the entrypoint it invokes. An engine that cannot answer is a <c>503</c> from the
/// endpoint, never a row of nulls: there is no partial identity worth serving.
/// </summary>
/// <param name="Version">The engine's version string, e.g. <c>3.18.0-rc4</c>.</param>
/// <param name="Path">The <c>kgsm</c> entrypoint this API invokes (<c>ApiOptions.KgsmPath</c>).</param>
/// <param name="Paths">The engine's directory layout, from <c>--paths</c> — null when the engine
/// answered its version but not its layout (an older engine without the command).</param>
public sealed record EngineInfo(
    string Version,
    string Path,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] EnginePaths? Paths);

/// <summary>
/// The engine's directory layout, selected from <c>kgsm --paths --json</c>: where the engine lives,
/// where its effective config is, and where instances and blueprints go. Each field is null when the
/// engine's answer lacked that key — never guessed.
/// </summary>
public sealed record EnginePaths(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Root,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ConfigFile,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? InstancesDir,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BlueprintsDir);
