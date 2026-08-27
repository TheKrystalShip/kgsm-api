using System.Text.Json;
using System.Text.Json.Serialization;

namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// The rules this API has stored for the reactor, as they sit on disk.
/// </summary>
/// <remarks>
/// ⚠ <b>What is stored and what is running are different questions.</b> This is the first; the second is
/// the leaf's own status, which reports the rules it could honour and names in <c>problems</c> the ones
/// it could not. An editor built only on the status would silently drop the rule somebody is halfway
/// through fixing, because a rule the leaf refuses appears in neither of its lists.
/// </remarks>
/// <param name="Managed">
/// Whether this API holds a rules file at all. <b>False is the ordinary answer on a host nobody has
/// edited</b> — the leaf runs the rules it ships — and is not an empty rule set.
/// </param>
/// <param name="Path">Where the file is written, whether or not one is there yet.</param>
/// <param name="Document">The file's content, verbatim, or null when there is none.</param>
public sealed record StoredReactorRules(
    [property: JsonPropertyName("managed")] bool Managed,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("document")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    JsonElement? Document);

/// <summary>
/// What came of storing a set of rules.
/// </summary>
/// <remarks>
/// ⚠ <b>A non-empty <see cref="Problems"/> is not a failure.</b> The file was stored and the rules the
/// leaf could honour are running; these are the ones it could not, in its own words. Reporting them as
/// an error would make a partly-good file impossible to save and impossible to fix.
/// </remarks>
/// <param name="Path">Where the rules were written.</param>
/// <param name="Problems">What the leaf could not honour, read back from it after the restart.</param>
/// <param name="Live">The ids of the rules the leaf is evaluating now.</param>
public sealed record ReactorRulesApplied(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("problems")] IReadOnlyList<string> Problems,
    [property: JsonPropertyName("live")] IReadOnlyList<string> Live);
