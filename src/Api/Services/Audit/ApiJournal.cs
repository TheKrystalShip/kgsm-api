using System.Text.Json;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Services;

namespace TheKrystalShip.Api.Services.Audit;

/// <summary>
/// Records what this API did itself, in this API's own event journal.
/// </summary>
/// <remarks>
/// <para>
/// <b>A producer records what that producer did.</b> Signing somebody in, changing an account's
/// authority, applying a leaf's configuration, releasing a backup archive — the engine runs nothing
/// for any of them, so nothing else on this host can honestly say they happened. They are written
/// here, beside the engine's journal and each leaf's, and the audit page is the merge.
/// </para>
/// <para>
/// <b>Nothing publishes from here.</b> This API already tails every journal it can discover, its own
/// included, and that tail is what shapes a row and pushes it to the realtime topic. Announcing from
/// the write site as well would send every one of these rows twice — and would have to re-derive the
/// journal position the id is built from, which the tail already knows. Writing is the whole of the
/// job.
/// </para>
/// <para>
/// <b>Facts, not sentences.</b> No summary, no severity, no formatted value: those are a reader's,
/// and <see cref="AuditMapping"/> builds them at read time from what is recorded here. It is what
/// lets the same fact be worded one way in the Control Panel and another in a chat surface, and what
/// keeps a wording improvement from applying only to rows written after it.
/// </para>
/// <para>
/// <b>Best-effort, always.</b> Every method swallows its failure after logging it. These are written
/// alongside actions that have already happened — a session already exists, a tier is already
/// changed — and turning a successful sign-in into an error because recording it did not work trades
/// a missing line for a broken door.
/// </para>
/// </remarks>
public sealed class ApiJournal(IEventJournalWriter writer, ILogger<ApiJournal> logger)
    : JournalRecorder(writer, logger)
{
    // ---- the types this producer owns ----------------------------------------------------------

    public const string LoginEvent = "auth_login";
    public const string LogoutEvent = "auth_logout";
    public const string ClusterSessionEvent = "auth_cluster_session";
    public const string SessionRevokedEvent = "auth_session_revoked";

    public const string UserProvisionedEvent = "user_provisioned";
    public const string UserApprovedEvent = "user_approved";
    public const string UserDisabledEvent = "user_disabled";
    public const string UserTierChangedEvent = "user_tier_changed";
    public const string UserDeletedEvent = "user_deleted";
    public const string UserPasswordChangedEvent = "user_password_changed";

    public const string IdentityLinkedEvent = "identity_linked";
    public const string IdentityUnlinkedEvent = "identity_unlinked";

    public const string ServiceConnectedEvent = "service_connected";
    public const string ServiceDisconnectedEvent = "service_disconnected";
    public const string ServiceConfigChangedEvent = "service_config_changed";
    public const string ServiceRestartedEvent = "service_restarted";

    public const string FileWrittenEvent = "file_written";
    public const string BackupDownloadedEvent = "backup_downloaded";

    /// <summary>
    /// Records a session beginning or ending.
    /// </summary>
    /// <remarks>
    /// The actor is the identity that arrived, not this API: nobody else was involved in a sign-in,
    /// and naming the component that minted the token would hide who came through the door.
    /// </remarks>
    public Task SessionAsync(
        string type, string? userId, string username, string identity, string? provider, string? tier,
        string? sid, string? userAgent, string? peerNode, string actor, string? origin,
        CancellationToken ct = default) =>
        WriteAsync(type, actor, origin, w =>
        {
            // Null when the caller did not have the account row in hand — a sign-out holds the token's
            // identity and nothing else, and deriving an id from the handle would record a lookup that
            // never happened.
            WriteNullable(w, "UserId", userId);
            w.WriteString("Username", username);
            w.WriteString("Identity", identity);
            WriteNullable(w, "Provider", provider);
            WriteNullable(w, "Tier", tier);
            WriteNullable(w, "Sid", sid);
            WriteNullable(w, "UserAgent", userAgent);

            // Present only on a cluster vouch, and the thing that says the proof was another node's
            // rather than this one's.
            WriteNullable(w, "PeerNode", peerNode);
        }, ct);

    /// <summary>Records sessions being torn down before they expired.</summary>
    public Task SessionRevokedAsync(
        string scope, string userId, string username, string? sid, int? count,
        string actor, string? origin, CancellationToken ct = default) =>
        WriteAsync(SessionRevokedEvent, actor, origin, w =>
        {
            w.WriteString("UserId", userId);
            w.WriteString("Username", username);
            w.WriteString("Scope", scope);

            // A single revocation names the session; a sweep names how many it ended. Neither
            // fabricates the other.
            WriteNullable(w, "Sid", sid);
            if (count is { } n)
                w.WriteNumber("Count", n);
            else
                w.WriteNull("Count");
        }, ct);

    /// <summary>
    /// Records an account being provisioned, approved, disabled, deleted, or having its authority or
    /// password changed.
    /// </summary>
    /// <remarks>
    /// ⚠ Takes no password parameter and never will. What is recorded is that a credential was set
    /// and by whom — the only signal an account takeover leaves — and the credential is not part of
    /// that fact.
    /// </remarks>
    public Task AccountAsync(
        string type, string userId, string username, string? fromTier = null, string? toTier = null,
        string? fromStatus = null, string? toStatus = null, bool? byHolder = null,
        string actor = "", string? origin = null, CancellationToken ct = default) =>
        WriteAsync(type, actor, origin, w =>
        {
            w.WriteString("UserId", userId);
            w.WriteString("Username", username);
            WriteNullable(w, "FromTier", fromTier);
            WriteNullable(w, "ToTier", toTier);
            WriteNullable(w, "FromStatus", fromStatus);
            WriteNullable(w, "ToStatus", toStatus);

            if (byHolder is { } held)
                w.WriteBoolean("ByHolder", held);
            else
                w.WriteNull("ByHolder");
        }, ct);

    /// <summary>Records an external identity being attached to or detached from an account.</summary>
    public Task IdentityAsync(
        string type, string userId, string username, string provider, string handle,
        string actor, string? origin, CancellationToken ct = default) =>
        WriteAsync(type, actor, origin, w =>
        {
            w.WriteString("UserId", userId);
            w.WriteString("Username", username);
            w.WriteString("Provider", provider);
            w.WriteString("Handle", handle);
        }, ct);

    /// <summary>Records a leaf's runtime provisioning being flipped.</summary>
    public Task ServiceProvisioningAsync(
        bool provisioned, string leaf, string? displayName, string actor, string? origin,
        CancellationToken ct = default) =>
        WriteAsync(provisioned ? ServiceConnectedEvent : ServiceDisconnectedEvent, actor, origin, w =>
        {
            w.WriteString("Leaf", leaf);
            WriteNullable(w, "DisplayName", displayName);
        }, ct);

    /// <summary>
    /// Records a configuration change applied to a leaf.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Keys only.</b> A leaf's configuration holds API keys, bot tokens and webhook URLs; what a
    /// key was set to is not part of the fact that it changed.
    /// </remarks>
    public Task ServiceConfigAsync(
        string leaf, string? displayName, IReadOnlyList<string> keys, string outcome,
        string actor, string? origin, CancellationToken ct = default) =>
        WriteAsync(ServiceConfigChangedEvent, actor, origin, w =>
        {
            w.WriteString("Leaf", leaf);
            WriteNullable(w, "DisplayName", displayName);

            w.WriteStartArray("Keys");
            foreach (string key in keys)
                w.WriteStringValue(key);
            w.WriteEndArray();

            w.WriteString("Outcome", outcome);
        }, ct);

    /// <summary>
    /// Records a leaf's unit being restarted, whether or not systemd performed it.
    /// </summary>
    /// <remarks>
    /// A refused restart is recorded as loudly as a performed one: it is exactly the case nobody was
    /// watching a screen for.
    /// </remarks>
    public Task ServiceRestartedAsync(
        string leaf, string? displayName, string unit, bool ok, string actor, string? origin,
        CancellationToken ct = default) =>
        WriteAsync(ServiceRestartedEvent, actor, origin, w =>
        {
            w.WriteString("Leaf", leaf);
            WriteNullable(w, "DisplayName", displayName);
            w.WriteString("Unit", unit);
            w.WriteBoolean("Ok", ok);
        }, ct);

    /// <summary>
    /// Records an instance file being edited through the file browser.
    /// </summary>
    /// <remarks>
    /// ⚠ The hash identifies the bytes; the bytes are never written here. An instance's configuration
    /// files hold rcon passwords, tokens and webhook URLs.
    /// </remarks>
    public Task FileWrittenAsync(
        string instance, string path, long sizeBytes, string? sha256, string actor, string? origin,
        CancellationToken ct = default) =>
        WriteAsync(FileWrittenEvent, actor, origin, w =>
        {
            w.WriteString("InstanceName", instance);
            w.WriteString("Path", path);
            w.WriteNumber("SizeBytes", sizeBytes);
            WriteNullable(w, "Sha256", sha256);
        }, ct);

    /// <summary>
    /// Records a backup archive being released off this host.
    /// </summary>
    /// <remarks>
    /// Written when the bytes were authorised to leave, not when somebody clicked: the fact worth
    /// keeping is that a copy of a world left this machine.
    /// </remarks>
    public Task BackupDownloadedAsync(
        string instance, string backupId, long sizeBytes, string? sha256, string actor, string? origin,
        CancellationToken ct = default) =>
        WriteAsync(BackupDownloadedEvent, actor, origin, w =>
        {
            w.WriteString("InstanceName", instance);
            w.WriteString("BackupId", backupId);
            w.WriteNumber("SizeBytes", sizeBytes);
            WriteNullable(w, "Sha256", sha256);
        }, ct);

    /// <summary>
    /// This API attributes nothing to itself.
    /// </summary>
    /// <remarks>
    /// ⚠ Every row here records something a <em>person</em> did — signed in, changed an account's
    /// authority, applied a leaf's configuration. The call site knows who; this class never guesses,
    /// and a default of <c>system:api</c> would put the server's own name on somebody's sign-in. An
    /// actor that did not reach the call site is a real null, which reads as unknown rather than as
    /// the machine.
    /// </remarks>
    protected override string? DefaultActor => null;

    /// <inheritdoc cref="DefaultActor"/>
    protected override string? DefaultOrigin => null;

    /// <summary>Appends one row, saying what was lost if it cannot.</summary>
    /// <remarks>
    /// The base logs the generic failure; this adds what the base cannot know — a row nobody will find
    /// later, for an action that did happen. Written alongside actions already completed: a session
    /// exists, a tier is already changed, so turning a successful sign-in into an error because
    /// recording it did not work would trade a missing line for a broken door.
    /// </remarks>
    private async Task WriteAsync(
        string type, string actor, string? origin, Action<Utf8JsonWriter> payload, CancellationToken ct)
    {
        if (!await RecordAsync(type, payload, actor, origin, ct).ConfigureAwait(false))
        {
            logger.LogWarning(
                "audit {Event} was not recorded — the action happened, the row did not",
                NormalizeType(type));
        }
    }
}
