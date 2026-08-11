using System.Text.Json.Serialization;

namespace TheKrystalShip.Api.Contracts;

/// <summary>GET/PATCH /integrations/webpush — the admin's view of the push channel. There is no
/// <c>webhook</c> block because there is no secret to paste: the host signs with a generated VAPID
/// pair and each browser mints its own credential.</summary>
public sealed record WebPushIntegrationView(
    string Provider,
    bool Configured,
    /// <summary>The VAPID public key, which a browser needs to subscribe. Public by design — the
    /// private half never leaves the host.</summary>
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PublicKey,
    bool Enabled,
    IReadOnlyList<IntegrationEventView> Events);

/// <summary>
/// The encrypted body a device actually receives — read by the service worker's <c>push</c> handler.
/// Kept small on purpose: push services cap the payload, and everything here is one tap from the panel
/// anyway. <paramref name="ServerId"/> is what lets a tap open the server it concerns.
/// </summary>
public sealed record WebPushPayload(
    string Title,
    string Body,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ServerId);

/// <summary>GET /push/key — what a browser needs before it can call <c>pushManager.subscribe</c>.</summary>
/// <param name="PublicKey">base64url VAPID public key, passed as <c>applicationServerKey</c>.</param>
/// <param name="Enabled">Whether the admin has the push channel switched on. A browser may subscribe
/// either way — but the panel says so, rather than letting someone opt in to silence.</param>
public sealed record PushKeyResponse(string PublicKey, bool Enabled);

/// <summary>POST /push/subscriptions — the browser's own <c>PushSubscription</c>, JSON-shaped exactly
/// as <c>subscription.toJSON()</c> produces it so the client forwards it unchanged.</summary>
public sealed record PushSubscribeRequest(string? Endpoint, PushKeys? Keys);

/// <param name="P256dh">base64url P-256 public key. Named <c>p256dh</c> on the wire.</param>
/// <param name="Auth">base64url 16-byte auth secret.</param>
public sealed record PushKeys(
    [property: JsonPropertyName("p256dh")] string? P256dh,
    string? Auth);

/// <summary>One of the caller's OWN devices. Never another user's, and never the keys — a device list
/// is for telling your phone from your laptop, not for re-deriving a credential.</summary>
public sealed record PushDeviceView(
    string Id,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? UserAgent,
    DateTimeOffset CreatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? LastSeenAt,
    bool Current);

/// <summary>GET /push/subscriptions — the caller's devices plus whether the channel is on at all.</summary>
public sealed record PushDevicesResponse(bool Enabled, IReadOnlyList<PushDeviceView> Devices);
