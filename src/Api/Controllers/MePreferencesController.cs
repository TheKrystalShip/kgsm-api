using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Preferences;
using TheKrystalShip.KGSM.Auth.Sessions;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// The caller's own preferences — the general per-account store the panel's editable surface is built
/// on. The dashboard layout is its first tenant and the UI theme its second, and this controller knows
/// what neither of them is: a key is an opaque string and a value is JSON stored and handed back
/// verbatim, so a new preference costs no backend change.
/// <para>
/// <b>Self-service, at <c>[Authorize]</c> rather than a tier</b> — the same gate as
/// <see cref="MeController"/>. These are a person's own settings, so somebody waiting on an admin (tier
/// <c>none</c>) still gets to arrange their own panel; there is deliberately no endpoint that reads or
/// writes anybody else's, not even for an admin.
/// </para>
/// <para>
/// <b>Preferences are per device, and the device names itself.</b> Every device-scoped request carries
/// its id in <c>X-Krystal-Device</c>, minted by the client and kept. A session id would be the obvious
/// alternative and is the wrong one: sessions are per-host and expire, so the same laptop signing in
/// again would be a new device and would lose its layout. A request that omits the header is refused
/// with <c>device_required</c> rather than defaulting to anything — the empty device is the synced
/// record's own slot, and silently writing there would publish one machine's arrangement to all of them.
/// </para>
/// <para>
/// <b>The account switch decides which slot every call touches.</b> Off, a device reads and writes its
/// own rows. On, they all read and write the one synced record. Enabling stamps the calling device as
/// the source and overwrites the others from it; disabling seeds every known device from the synced
/// record, so nobody lands on an empty dashboard the moment the switch moves.
/// </para>
/// </summary>
/// <remarks>
/// Each write increments the key's <c>version</c>, monotonic across the whole account. It is carried
/// from the start although nothing propagates between nodes yet, because a merge key retrofitted onto
/// rows that already exist means inventing a version for every one of them.
/// </remarks>
[ApiController]
[Route("api/v1/me/preferences")]
[Authorize]
public sealed class MePreferencesController(UserPreferenceStore store) : ControllerBase
{
    /// <summary>The header a client names its device in.</summary>
    public const string DeviceHeader = "X-Krystal-Device";

    /// <summary>
    /// The largest value this store accepts, in bytes of JSON. A dashboard layout is a few kilobytes of
    /// descriptors; the bound is what stops the one endpoint that takes arbitrary client JSON from
    /// becoming somewhere to keep arbitrary client data.
    /// </summary>
    public const int MaxValueBytes = 64 * 1024;

    private const int MaxKeyLength = 128;
    private const int MaxDeviceLength = 64;

    private IActionResult Error(int status, string code, string message) =>
        StatusCode(status, new ErrorEnvelope(new ErrorBody(code, message, null)));

    private string? UserId() =>
        User.Identity is ClaimsIdentity ci && SessionClaims.ReadIdentity(ci) is { } id ? id.Handle : null;

    /// <summary>
    /// The calling device, or an error result naming what is wrong with it. A blank header and a
    /// missing one are the same answer: the client has not said which machine this is.
    /// </summary>
    private bool TryDevice(out string device, out IActionResult? failure)
    {
        device = Request.Headers.TryGetValue(DeviceHeader, out var raw) ? raw.ToString().Trim() : "";
        if (device.Length == 0)
        {
            failure = Error(StatusCodes.Status400BadRequest, "device_required",
                $"{DeviceHeader} is required — preferences are stored per device");
            return false;
        }
        if (device.Length > MaxDeviceLength || !IsSafeToken(device))
        {
            failure = Error(StatusCodes.Status400BadRequest, "bad_request",
                $"{DeviceHeader} must be at most {MaxDeviceLength} characters of [A-Za-z0-9._:-]");
            return false;
        }
        failure = null;
        return true;
    }

    /// <summary>The character set a device id and a preference key share. Narrow on purpose: both are
    /// echoed back and one of them is a route segment.</summary>
    private static bool IsSafeToken(string value)
    {
        foreach (char c in value)
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-' or ':'))
                return false;
        return true;
    }

    private static PreferenceRecordDto ToDto(PreferenceRow row) => new(
        row.Key, JsonSerializer.Deserialize<JsonElement>(row.Value), row.Version, row.OriginDevice, row.Updated);

    private static SyncStateDto ToDto(SyncState sync) => new(sync.Enabled, sync.SourceDevice, sync.Updated);

    /// <summary>
    /// The effective set for the calling device: the synced record when sync is on, this device's own
    /// rows when it is off.
    /// </summary>
    /// <remarks>
    /// The header is required here too, even though a synced read does not depend on which device asks.
    /// A client cannot know the switch's position before the answer arrives, so making the requirement
    /// conditional on server state would mean an error that appears and disappears on its own.
    /// </remarks>
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        if (UserId() is not { } user)
            return Error(StatusCodes.Status401Unauthorized, "unauthorized", "no session");
        if (!TryDevice(out string device, out IActionResult? failure))
            return failure!;

        (SyncState sync, IReadOnlyList<PreferenceRow> rows) = await store.EffectiveAsync(user, device, ct);
        return Ok(new PreferencesResponse(device, ToDto(sync),
            rows.ToDictionary(r => r.Key, ToDto, StringComparer.Ordinal)));
    }

    /// <summary>
    /// Store one preference. It lands on this device's rows, or on the synced record when the account's
    /// switch is on, and takes the key's next version either way.
    /// </summary>
    [HttpPut("{key}")]
    public async Task<IActionResult> Put(string key, [FromBody] PreferenceWriteRequest? body, CancellationToken ct)
    {
        if (UserId() is not { } user)
            return Error(StatusCodes.Status401Unauthorized, "unauthorized", "no session");
        if (!TryDevice(out string device, out IActionResult? failure))
            return failure!;
        if (key.Length == 0 || key.Length > MaxKeyLength || !IsSafeToken(key))
            return Error(StatusCodes.Status400BadRequest, "bad_request",
                $"key must be at most {MaxKeyLength} characters of [A-Za-z0-9._:-]");
        if (body?.Value is not { } value)
            return Error(StatusCodes.Status400BadRequest, "bad_request", "value is required");

        string json = value.GetRawText();
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxValueBytes)
            return Error(StatusCodes.Status413PayloadTooLarge, "value_too_large",
                $"a preference value must be at most {MaxValueBytes} bytes of JSON");

        PreferenceRow? row = await store.SetAsync(user, device, key, json, ct);
        // A slot that is full refuses the NEW key rather than evicting one — nothing here knows which of
        // somebody's preferences matters least.
        return row is null
            ? Error(StatusCodes.Status409Conflict, "too_many_preferences",
                $"this device already holds {UserPreferenceStore.MaxKeysPerSlot} preferences")
            : Ok(ToDto(row));
    }

    /// <summary>The account's sync switch. Account-scoped, so this is the one call here that needs no
    /// device — reading whether preferences follow the person says nothing about which machine asked.</summary>
    [HttpGet("sync")]
    public async Task<IActionResult> GetSync(CancellationToken ct)
    {
        if (UserId() is not { } user)
            return Error(StatusCodes.Status401Unauthorized, "unauthorized", "no session");
        return Ok(ToDto(await store.SyncStateAsync(user, ct)));
    }

    /// <summary>
    /// Move the switch. Enabling makes the calling device the source and overwrites every other device
    /// from it; disabling seeds every known device from the synced record.
    /// </summary>
    /// <remarks>
    /// The device is required in both directions because both are written from one machine's point of
    /// view: enabling names the arrangement that wins, disabling names the device the seeded rows are
    /// attributed to.
    /// </remarks>
    [HttpPut("sync")]
    public async Task<IActionResult> PutSync([FromBody] SyncToggleRequest? body, CancellationToken ct)
    {
        if (UserId() is not { } user)
            return Error(StatusCodes.Status401Unauthorized, "unauthorized", "no session");
        if (!TryDevice(out string device, out IActionResult? failure))
            return failure!;
        if (body?.Enabled is not { } enabled)
            return Error(StatusCodes.Status400BadRequest, "bad_request", "enabled is required");

        SyncState state = enabled
            ? await store.EnableSyncAsync(user, device, ct)
            : await store.DisableSyncAsync(user, device, ct);
        return Ok(ToDto(state));
    }
}
