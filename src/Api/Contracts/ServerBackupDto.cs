namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// One backup snapshot of an instance (Tier-1 ops — an element of <c>GET /servers/{id}/backups</c>). kgsm's
/// <c>instances backups</c> lists backups by <strong>name only</strong> (one per line), so <see cref="Name"/>
/// is the only honest field. Size / timestamp / type are NOT reported by the engine today — they are omitted
/// rather than fabricated (the never-invent-a-value invariant). The SPA's adapter renders what is present and
/// degrades the rest.
/// </summary>
public sealed record ServerBackup(string Name);

/// <summary>The <c>GET /servers/{id}/backups</c> body: this instance's snapshots (newest-first as the engine
/// lists them) plus the owning <c>serverId</c>.</summary>
public sealed record ServerBackupList(string ServerId, IReadOnlyList<ServerBackup> Backups);

/// <summary>
/// The request body for <c>POST /servers/{id}/backups/restore</c> (Tier-1 ops). <see cref="Backup"/> is the
/// snapshot name to restore (required — one of the names from the list). <see cref="Origin"/> is the driving
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
