using TheKrystalShip.Api.Contracts;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Api.Services.Aggregation;

/// <summary>
/// The single backup-manifest → honest-DTO mapper, shared by the <c>GET /servers/{id}/backups</c> listing
/// and the <see cref="BackupCache"/> that puts the newest snapshot on the <see cref="Server"/> DTO.
/// Centralizing it means a backup rendered on a server card describes the same facts, in the same units,
/// as the same backup rendered in the list — the two surfaces cannot silently diverge.
/// </summary>
/// <remarks>
/// The manifest is the only source of truth about a backup, so every field is a passthrough: a fact the
/// manifest does not carry stays null rather than being defaulted or derived. In particular
/// <c>sha256</c> is null for an uncompressed backup (a directory tree has no single digest) and an empty
/// <c>sources</c> list becomes null rather than an empty array, so a surface can tell "the manifest
/// recorded no sources" from "the manifest recorded these sources".
/// </remarks>
internal static class ServerBackupMapping
{
    /// <summary>Map one backup's manifest record to the wire DTO.</summary>
    public static ServerBackup FromManifest(InstanceBackup m) =>
        new(m.Id,
            m.CreatedAt,
            m.Version,
            m.SizeBytes,
            m.FileCount,
            m.Compressed,
            m.Consistency,
            m.Sources.Count == 0 ? null : m.Sources,
            m.Sha256);

    /// <summary>
    /// The record for a backup the engine lists but whose manifest could not be read — it exists and is
    /// named, and every other fact about it is honestly absent.
    /// </summary>
    public static ServerBackup IdOnly(string backupId) => new(backupId);
}
