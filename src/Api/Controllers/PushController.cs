using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Integrations;
using TheKrystalShip.Api.Services.Integrations.WebPush;
using TheKrystalShip.KGSM.Auth.Sessions;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// A person's own push devices.
/// <para>
/// Separate from <see cref="IntegrationsController"/>, and at <b>viewer</b> rather than admin, because
/// these are two different things wearing one word. Admin configures the CHANNEL — is push on, which
/// events use it — and that stays on the integrations route. Here, any signed-in person registers and
/// revokes their OWN devices, which nobody else should be able to do for them.
/// </para>
/// <para>
/// Every read and write is scoped to the caller's subject. There is deliberately no endpoint that lists
/// or revokes another user's devices, not even for an admin: the row is a capability to push to
/// somebody's phone.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/push")]
[Authorize(Policy = AuthPolicy.Viewer)]
public sealed class PushController(
    PushSubscriptionStore subscriptions,
    PushPreferenceStore preferences,
    VapidKeyStore vapid,
    IntegrationStore integrations,
    ILogger<PushController> logger) : ControllerBase
{
    private IActionResult Error(int status, string code, string message) =>
        StatusCode(status, new ErrorEnvelope(new ErrorBody(code, message, null)));

    private string? Subject() =>
        User.Identity is ClaimsIdentity ci && SessionClaims.ReadIdentity(ci) is { } id ? id.Subject : null;

    private string? Username() =>
        User.Identity is ClaimsIdentity ci && SessionClaims.ReadIdentity(ci) is { } id ? id.Username : null;

    /// <summary>
    /// The host's VAPID public key, generated on first ask. A browser cannot subscribe without it.
    /// </summary>
    [HttpGet("key")]
    public async Task<IActionResult> GetKey(CancellationToken ct)
    {
        VapidKeyPair keys = await vapid.EnsureAsync(ct);
        IntegrationRecord record = await integrations.GetAsync(VapidKeyStore.ProviderId, ct);
        return Ok(new PushKeyResponse(keys.PublicKey, record.Enabled));
    }

    /// <summary>The caller's own devices.</summary>
    [HttpGet("subscriptions")]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (Subject() is not { } subject) return Error(StatusCodes.Status401Unauthorized, "unauthorized", "no valid session");
        IntegrationRecord record = await integrations.GetAsync(VapidKeyStore.ProviderId, ct);
        IReadOnlyList<PushSubscriptionEntity> rows = await subscriptions.ForUserAsync(subject, ct);

        // "Current" is resolved against the endpoint the caller names, so the panel can mark THIS
        // browser in a list of several without the client having to match opaque ids itself.
        string? current = Request.Query.TryGetValue("endpoint", out var e) ? e.ToString() : null;
        var devices = rows.Select(r => new PushDeviceView(
            Id: Fingerprint(r.Endpoint),
            UserAgent: r.UserAgent,
            CreatedAt: r.CreatedAt,
            LastSeenAt: r.LastSeenAt,
            Current: current is not null && string.Equals(r.Endpoint, current, StringComparison.Ordinal))).ToList();

        return Ok(new PushDevicesResponse(record.Enabled, devices));
    }

    /// <summary>
    /// Register (or refresh) this browser. Idempotent on the endpoint, which the user agent owns — a
    /// browser that re-subscribes must not end up with two rows and two copies of every notification.
    /// </summary>
    [HttpPost("subscriptions")]
    public async Task<IActionResult> Subscribe([FromBody] PushSubscribeRequest? body, CancellationToken ct)
    {
        if (Subject() is not { } subject) return Error(StatusCodes.Status401Unauthorized, "unauthorized", "no valid session");

        if (body is null || string.IsNullOrWhiteSpace(body.Endpoint))
            return Error(StatusCodes.Status400BadRequest, "bad_request", "endpoint is required");
        if (!Uri.TryCreate(body.Endpoint, UriKind.Absolute, out Uri? endpoint) || endpoint.Scheme != Uri.UriSchemeHttps)
            return Error(StatusCodes.Status400BadRequest, "bad_request", "endpoint must be an absolute https URL");
        if (body.Keys is null || string.IsNullOrWhiteSpace(body.Keys.P256dh) || string.IsNullOrWhiteSpace(body.Keys.Auth))
            return Error(StatusCodes.Status400BadRequest, "bad_request", "keys.p256dh and keys.auth are required");

        // Validate the key material HERE rather than discovering it at send time: a malformed
        // subscription that is accepted now fails silently later, when nobody is watching.
        try
        {
            if (WebPushCrypto.FromBase64Url(body.Keys.P256dh) is not { Length: 65 } p || p[0] != 0x04)
                return Error(StatusCodes.Status400BadRequest, "bad_request", "keys.p256dh must be a 65-byte uncompressed P-256 point");
            if (WebPushCrypto.FromBase64Url(body.Keys.Auth) is not { Length: 16 })
                return Error(StatusCodes.Status400BadRequest, "bad_request", "keys.auth must be 16 bytes");
        }
        catch (FormatException)
        {
            return Error(StatusCodes.Status400BadRequest, "bad_request", "keys must be base64url");
        }

        await subscriptions.SaveAsync(new PushSubscriptionEntity
        {
            Endpoint = body.Endpoint,
            UserSubject = subject,
            Username = Username(),
            P256dh = body.Keys.P256dh,
            Auth = body.Keys.Auth,
            UserAgent = Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? Truncate(ua, 200) : null,
            CreatedAt = DateTimeOffset.UtcNow,
        }, ct);

        // Endpoint-only: the log must never carry the keys, and the endpoint's host is enough to tell
        // which push service a device is on when debugging.
        logger.LogInformation("push device registered for {User} via {Service}", subject, endpoint.Host);
        return NoContent();
    }

    /// <summary>Revoke one of the caller's own devices. 404 when it is not theirs — an endpoint that
    /// belongs to someone else must not be distinguishable from one that does not exist.</summary>
    [HttpDelete("subscriptions")]
    public async Task<IActionResult> Unsubscribe([FromQuery] string? endpoint, CancellationToken ct)
    {
        if (Subject() is not { } subject) return Error(StatusCodes.Status401Unauthorized, "unauthorized", "no valid session");
        if (string.IsNullOrWhiteSpace(endpoint))
            return Error(StatusCodes.Status400BadRequest, "bad_request", "endpoint is required");

        bool removed = await subscriptions.DeleteAsync(subject, endpoint, ct);
        return removed ? NoContent() : Error(StatusCodes.Status404NotFound, "not_found", "no such device for this account");
    }

    /// <summary>
    /// The caller's own notification choices, over the host's catalog.
    /// </summary>
    [HttpGet("preferences")]
    public async Task<IActionResult> GetPreferences(CancellationToken ct)
    {
        if (Subject() is not { } subject) return Error(StatusCodes.Status401Unauthorized, "unauthorized", "no valid session");
        IntegrationRecord record = await integrations.GetAsync(VapidKeyStore.ProviderId, ct);
        IReadOnlyDictionary<string, bool> mine = await preferences.ForUserAsync(subject, ct);

        var hostRules = record.Events.ToDictionary(e => e.Id, StringComparer.Ordinal);
        var events = NotificationCatalog.Events.Select(c => new PushPreferenceView(
            c.Id, c.Title, c.Description,
            // No stored choice means ON: subscribing a device is already the opt-in, and a catalog
            // event added later should arrive rather than sit silently off until it is discovered.
            Enabled: !mine.TryGetValue(c.Id, out bool want) || want,
            AvailableOnHost: !hostRules.TryGetValue(c.Id, out NotificationRule? r) || r.Enabled)).ToList();

        return Ok(new PushPreferencesResponse(record.Enabled, events));
    }

    /// <summary>Change some of the caller's own choices. Sparse — absent ids are untouched.</summary>
    [HttpPatch("preferences")]
    public async Task<IActionResult> PatchPreferences([FromBody] PushPreferencePatch? body, CancellationToken ct)
    {
        if (Subject() is not { } subject) return Error(StatusCodes.Status401Unauthorized, "unauthorized", "no valid session");
        if (body?.Events is null || body.Events.Count == 0)
            return Error(StatusCodes.Status400BadRequest, "bad_request", "events is required");

        var choices = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (PushPreferenceChange change in body.Events)
        {
            // Reject an unknown id rather than storing a row nothing will ever read.
            if (!NotificationCatalog.IsKnown(change.Id))
                return Error(StatusCodes.Status400BadRequest, "bad_request", $"unknown event '{change.Id}'");
            choices[change.Id] = change.Enabled;
        }

        await preferences.SetAsync(subject, choices, ct);
        return await GetPreferences(ct);
    }

    /// <summary>A short, stable, non-reversible handle for a device row. The endpoint itself is a
    /// capability to push to that browser, so it never leaves the server.</summary>
    private static string Fingerprint(string endpoint)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(WebPushCrypto.Utf8(endpoint));
        return Convert.ToHexString(hash.AsSpan(0, 6)).ToLowerInvariant();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
