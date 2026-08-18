namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// One backup of an instance (Tier-1 ops — an element of <c>GET /servers/{id}/backups</c>).
/// </summary>
/// <remarks>
/// <see cref="Name"/> is the backup's opaque engine id and the value a restore takes; nothing parses it.
/// Every other field comes from the backup's own manifest, so each one is measured rather than derived.
/// A field the manifest does not carry stays <c>null</c> — never defaulted, never fabricated (the
/// never-invent-a-value invariant), and the SPA renders what is present and omits the rest.
/// <see cref="Sha256"/> is null for an uncompressed backup: it is a directory tree rather than a single
/// artifact, so there is no one digest to report.
/// </remarks>
/// <param name="Name">The backup's opaque id — pass it to restore.</param>
/// <param name="CreatedAt">When the backup was taken (UTC).</param>
/// <param name="Version">The game-server version the backup captured.</param>
/// <param name="SizeBytes">Size of the backup's payload on disk.</param>
/// <param name="FileCount">Number of files captured.</param>
/// <param name="Compressed">Whether the payload is an archive rather than a directory tree.</param>
/// <param name="Consistency">How consistent the capture is — <c>cold</c> means the instance was stopped
/// for its duration, the only mode the engine takes today.</param>
/// <param name="Sources">Which of the instance's directories the backup holds (<c>install</c>, <c>saves</c>).</param>
/// <param name="Sha256">Digest of the archive, verified before a restore; null when not applicable.</param>
/// <param name="Reason">Why the backup was taken — <c>manual</c>, <c>scheduled</c>, <c>pre-update</c>,
/// <c>pre-restore</c> or <c>incident</c>. A fact fixed at capture. Null when the manifest records none,
/// which is <b>unknown</b>: a backup written before the field existed cannot be identified after the fact,
/// so a surface says so rather than showing a default.</param>
/// <param name="Retention"><c>prunable</c> or <c>pinned</c>. A policy, not a fact — see <see cref="Pinned"/>.
/// Null when the manifest records none, which behaves as prunable.</param>
/// <param name="Pinned">Whether retention will skip this backup. Resolved from
/// <paramref name="Retention"/> so the SPA never compares the string, and never null: an absent
/// retention is not pinned.</param>
public sealed record ServerBackup(
    string Name,
    DateTimeOffset? CreatedAt = null,
    string? Version = null,
    long? SizeBytes = null,
    long? FileCount = null,
    bool? Compressed = null,
    string? Consistency = null,
    IReadOnlyList<string>? Sources = null,
    string? Sha256 = null,
    string? Reason = null,
    string? Retention = null,
    bool Pinned = false);

/// <summary>The <c>GET /servers/{id}/backups</c> body: this instance's snapshots (newest-first as the engine
/// lists them) plus the owning <c>serverId</c>.</summary>
public sealed record ServerBackupList(string ServerId, IReadOnlyList<ServerBackup> Backups);

/// <summary>
/// The request body for <c>POST /servers/{id}/backups/restore</c> (Tier-1 ops). <see cref="Backup"/> is the
/// backup id to restore (required — one of the ids from the list). <see cref="Origin"/> is the driving
/// surface stamped onto the engine call (like <see cref="CommandRequest.Origin"/>); absent ⇒ <c>api</c>.
/// Restore is async — the endpoint returns a <see cref="Job"/> and progress arrives on the <c>jobs</c> topic.
/// </summary>
public sealed record RestoreBackupRequest(string? Backup, string? Origin = null);

/// <summary>
/// The request body for <c>POST /servers/{id}/backups</c> (create — Tier-1 ops). The snapshot name is the
/// engine's to assign (kgsm derives it from the instance + timestamp), so the only field is <see cref="Origin"/>
/// (the driving surface; absent ⇒ <c>api</c>). Async — returns a <see cref="Job"/>; the new backup appears on a
/// subsequent <c>GET /servers/{id}/backups</c> and a <c>backup.create</c> audit row lands (from the kgsm echo).
/// </summary>
public sealed record CreateBackupRequest(string? Origin = null);

/// <summary>
/// The request body for <c>POST /servers/{id}/backups/{backupId}/pin</c> and <c>…/unpin</c>. The only field
/// is <see cref="Origin"/> (the driving surface; absent ⇒ <c>api</c>) — which backup, and which direction,
/// are already in the route.
/// </summary>
public sealed record BackupRetentionRequest(string? Origin = null);

/// <summary>
/// The response to <c>POST /servers/{id}/backups/{backupId}/download-ticket</c>: a short-lived handle
/// plus the URL to hand the browser.
/// </summary>
/// <remarks>
/// <see cref="Url"/> is server-relative on purpose. The SPA drives a cluster and already resolves each
/// node's origin exactly; an absolute URL built here would have to guess which of this host's addresses
/// the browser can actually reach — the same conflation that keeps the assistant's public origin a
/// separate setting from its loopback one.
/// </remarks>
/// <param name="Ticket">The opaque handle — a bearer credential until it expires.</param>
/// <param name="Url">Where to send the browser, relative to this API's root.</param>
/// <param name="ExpiresAt">When the ticket stops being redeemable.</param>
/// <param name="SizeBytes">The archive's size, so a caller can warn before starting a large transfer.</param>
/// <param name="Sha256">The manifest's digest of the archive, for verifying what lands.</param>
public sealed record BackupDownloadTicketResponse(
    string Ticket,
    string Url,
    DateTimeOffset ExpiresAt,
    long SizeBytes,
    string? Sha256);
