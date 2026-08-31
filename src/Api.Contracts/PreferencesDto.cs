using System.Text.Json;

namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// <c>GET /me/preferences</c> — what the calling device should be reading: the synced record when the
/// account's switch is on, that device's own rows when it is off.
/// </summary>
/// <param name="DeviceId">The device the answer was resolved for — echoed back so a client can tell a
/// stale reply from one for the device it just minted.</param>
/// <param name="Sync">The account's switch, so the panel can render the settings card from the same
/// read rather than asking twice.</param>
/// <param name="Preferences">Key → the stored preference. Keys are opaque: this API stores what it is
/// handed under the name it is handed, and a key it has never seen behaves exactly like one it has.</param>
public sealed record PreferencesResponse(
    string DeviceId, SyncStateDto Sync, IReadOnlyDictionary<string, PreferenceRecordDto> Preferences);

/// <summary>
/// One stored preference. <paramref name="Value"/> is the client's own JSON, handed back verbatim.
/// </summary>
/// <param name="Version">Monotonic per (account, key) — the merge key a cluster converges on. Not a
/// clock: wall-clock last-write-wins hands permanent victory to whichever node's clock runs fastest.</param>
/// <param name="OriginDevice">The device whose write produced this version — the tiebreak at equal
/// versions, compared lexically so every node reaches the same answer.</param>
/// <param name="Updated">When it was written. <b>Display only</b>; nothing decides anything on it.</param>
public sealed record PreferenceRecordDto(
    string Key, JsonElement Value, long Version, string OriginDevice, DateTimeOffset Updated);

/// <summary>Whether an account's preferences follow the person across their devices, and which device
/// switched that on. <paramref name="SourceDevice"/>/<paramref name="Updated"/> are <c>null</c> for an
/// account that has never touched the switch — absent, not fabricated.</summary>
public sealed record SyncStateDto(bool Enabled, string? SourceDevice, DateTimeOffset? Updated);

/// <summary><c>PUT /me/preferences/{key}</c> — the preference to store, as arbitrary JSON.</summary>
public sealed record PreferenceWriteRequest(JsonElement? Value);

/// <summary><c>PUT /me/preferences/sync</c> — the switch's new position.</summary>
public sealed record SyncToggleRequest(bool? Enabled);
