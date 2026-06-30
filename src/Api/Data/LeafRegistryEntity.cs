namespace TheKrystalShip.Api.Data;

/// <summary>
/// One provisionable leaf's <strong>runtime provisioning</strong> row — the persisted, runtime-mutable
/// half of "is this leaf connected on this host" (the leaf-runtime-provisioning feature, Phase 1). One row
/// per provisionable leaf id (<c>monitor</c>/<c>watchdog</c>/<c>assistant</c>/<c>firewall</c>), keyed by
/// <see cref="LeafId"/>. The row's <see cref="Provisioned"/> flag is what an admin flips at runtime via the
/// Services panel (connect/disconnect), so it moves the capability set off the immutable startup
/// <see cref="ApiOptions"/> into a DB-backed registry the <see cref="Services.Leaves.LeafHealthMonitor"/>
/// reads each tick.
/// <para>
/// It joins <see cref="AuditEntry"/>/<see cref="HostSettingsEntity"/> as the API's own operational
/// metadata. ⚠ <b>EnsureCreated, NOT a migration</b> (project dev authority — see <see cref="AppDbContext"/>):
/// because <c>EnsureCreated</c> no-ops on an existing DB, <see cref="Services.Leaves.LeafRegistry"/> ALSO
/// issues an idempotent <c>CREATE TABLE IF NOT EXISTS</c> so this table appears on an already-deployed host
/// <em>without</em> wiping the append-only audit log that shares the DB.
/// </para>
/// </summary>
public sealed class LeafRegistryEntity
{
    /// <summary>The provisionable leaf id (<c>monitor</c>/<c>watchdog</c>/<c>assistant</c>/<c>firewall</c>) —
    /// the primary key. One row per provisionable leaf.</summary>
    public string LeafId { get; set; } = "";

    /// <summary>Whether the leaf is connected on this host (the runtime-flippable capability flag).</summary>
    public bool Provisioned { get; set; }

    /// <summary>An optional per-leaf endpoint override (socket path / URL). <strong>Forward-compat</strong>:
    /// stored but NOT yet wired into the leaf clients' transport in Phase 1 (the endpoint comes from the
    /// configured-or-default <see cref="ApiOptions"/> value). Null ⇒ use the config/default endpoint.</summary>
    public string? Endpoint { get; set; }

    /// <summary>When the provisioning was last written (UTC).</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
