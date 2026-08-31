using System.Text.Json.Serialization;

namespace TheKrystalShip.Api.Contracts;

/// <summary>GET/PATCH /integrations/webpush — the admin's view of the push channel. There is no
/// <c>webhook</c> block because there is no secret to paste: the host signs with a generated VAPID
/// pair and each browser mints its own credential.</summary>
/// <param name="Provider">The channel's identifier, <c>webpush</c>.</param>
/// <param name="Configured">Whether this host holds a VAPID pair to send with.</param>
/// <param name="PublicKey">The VAPID public key, which a browser needs to subscribe. Public by design
/// — the private half never leaves the host.</param>
/// <param name="Enabled">Whether the admin has the channel switched on.</param>
/// <param name="Events">Which events this channel is configured to deliver.</param>
public sealed record WebPushIntegrationView(
    string Provider,
    bool Configured,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PublicKey,
    bool Enabled,
    IReadOnlyList<IntegrationEventView> Events);

/// <summary>
/// The encrypted body a device actually receives — read by the service worker's <c>push</c> handler.
/// Kept small on purpose: push services cap the payload, and everything here is one tap from the panel
/// anyway. <paramref name="ServerId"/> is what lets a tap open the server it concerns.
/// </summary>
/// <param name="Event">The catalog event id, so the worker can route a tap at something other than a
/// server — the panel owns its own routes, and a URL built here would be this API guessing at them.</param>
/// <param name="Tag">The device-side coalescing key: a second notification carrying it replaces the first
/// on the lock screen instead of stacking under it. It names the <em>subject</em> — one host watches
/// several conditions at once, so keying this on the host would let a disk warning overwrite a temperature
/// one. Absent falls back to the worker's own per-server key.</param>
/// <param name="Api">This host's public origin, so a button's redemption reaches the API that staged
/// it. A browser drives several nodes but runs one service worker, on whichever node serves the panel —
/// without this it would answer a push from one host by calling another, which knows nothing about the
/// handle. Absent when the host has no public address configured, and the worker then falls back to its
/// own origin.</param>
/// <param name="Actions">The buttons to draw, in order. Each carries the opaque handle that redeems it
/// and nothing else — what would be done stays on the host. Empty for the events that offer nothing, and
/// omitted entirely for a device that reported it renders none.</param>
public sealed record WebPushPayload(
    string Title,
    string Body,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ServerId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Event = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Tag = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Api = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WebPushAction>? Actions = null);

/// <summary>One notification button on the wire.</summary>
/// <param name="Handle">The staged action's opaque id. It is also the button's <c>action</c> value, so
/// the worker hands back exactly what it was given and needs no map of its own.</param>
/// <param name="Title">What the button says.</param>
public sealed record WebPushAction(string Handle, string Title);

/// <summary>POST /notifications/actions/{handle} — the service worker redeeming a button.</summary>
/// <param name="Endpoint">This device's own push endpoint, read back from <c>getSubscription()</c>. The
/// handle is staged against it, so a handle without its device redeems nothing.</param>
public sealed record PushActionRedeemRequest(string? Endpoint);

/// <summary>The outcome, in the words the worker shows on the follow-up notification.</summary>
/// <param name="Message">What actually happened — never more than was established. Asking kgsm to
/// update a server is not the same as the server having updated, and only the first has happened here.</param>
public sealed record PushActionResult(bool Ok, string Message);

/// <summary>GET /push/key — what a browser needs before it can call <c>pushManager.subscribe</c>.</summary>
/// <param name="PublicKey">base64url VAPID public key, passed as <c>applicationServerKey</c>.</param>
/// <param name="Enabled">Whether the admin has the push channel switched on. A browser may subscribe
/// either way — but the panel says so, rather than letting someone opt in to silence.</param>
public sealed record PushKeyResponse(string PublicKey, bool Enabled);

/// <summary>POST /push/subscriptions — the browser's own <c>PushSubscription</c>, JSON-shaped exactly
/// as <c>subscription.toJSON()</c> produces it so the client forwards it unchanged.</summary>
/// <param name="MaxActions">How many notification buttons this browser renders
/// (<c>Notification.maxActions</c>). Reported rather than inferred: the platform that renders none is
/// also the one whose user-agent is most often imitated. Absent is treated as none.</param>
public sealed record PushSubscribeRequest(string? Endpoint, PushKeys? Keys, int? MaxActions = null);

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

/// <summary>
/// One catalog event as it appears on a person's own notification settings.
/// <para>
/// <paramref name="Enabled"/> is THEIR choice; <paramref name="AvailableOnHost"/> is the admin's
/// host-wide rule for the push channel. Both are reported because both gate delivery, and showing
/// only the personal one would let somebody switch an event on and hear nothing with no explanation.
/// </para>
/// </summary>
public sealed record PushPreferenceView(
    string Id, string Title, string Description, bool Enabled, bool AvailableOnHost);

/// <summary>GET /push/preferences — the whole catalog with this caller's choices applied.</summary>
/// <param name="Enabled">Whether the push channel is on for the host at all.</param>
/// <param name="QuietHours">This caller's quiet window. Always present, defaulted rather than null, so a
/// client renders the same card whether or not somebody has ever opened it.</param>
public sealed record PushPreferencesResponse(
    bool Enabled, IReadOnlyList<PushPreferenceView> Events, PushQuietHoursView QuietHours);

/// <summary>
/// A quiet window, on the wire.
/// </summary>
/// <param name="Start">Local time the window opens, <c>HH:mm</c>.</param>
/// <param name="End">Local time it closes, <c>HH:mm</c>. Earlier than <paramref name="Start"/> means it
/// wraps midnight, which is what a night usually does.</param>
/// <param name="TimeZone">The IANA zone the two times are read in, as the browser reported it.</param>
/// <param name="MinSeverity">A <c>PushQuietFloor</c> value — what still gets through.</param>
/// <param name="Resolvable">Whether this host can resolve <paramref name="TimeZone"/> at all. False means
/// the window is not being applied, and the panel says so rather than showing a setting that does nothing.</param>
public sealed record PushQuietHoursView(
    bool Enabled, string Start, string End, string TimeZone, string MinSeverity, bool Resolvable);

/// <summary>PUT /push/quiet-hours — the whole window, replaced.</summary>
public sealed record PushQuietHoursRequest(
    bool? Enabled, string? Start, string? End, string? TimeZone, string? MinSeverity);

/// <summary>PATCH /push/preferences — a sparse update; only the ids present change.</summary>
public sealed record PushPreferencePatch(IReadOnlyList<PushPreferenceChange>? Events);

public sealed record PushPreferenceChange(string Id, bool Enabled);
