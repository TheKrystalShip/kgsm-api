namespace TheKrystalShip.Api.Data;

/// <summary>
/// This host's <strong>operator-editable identity overrides</strong> — the runtime-mutable half of the
/// host identity card (region + display label). One logical row, keyed by <see cref="Id"/> = this host's
/// id (the API is per-host, so there is only ever one row). A <see langword="null"/> column means "fall
/// back to config" (the <c>KGSM_API_HOST_LABEL</c>/<c>KGSM_API_REGION</c> deploy-time default), never a
/// fabricated value — so config seeds the default and a <c>PATCH /hosts/{id}</c> overrides it live.
/// <para>
/// It joins <see cref="AuditEntry"/>/<see cref="IntegrationEntity"/>/<see cref="RawgEntry"/> as the API's
/// own operational metadata (the domain itself is live-scraped, never stored). ⚠ <b>EnsureCreated, NOT a
/// migration</b> (the project's dev authority — see <see cref="AppDbContext"/>): because
/// <c>EnsureCreated</c> no-ops on an existing DB, <see cref="Services.Aggregation.HostSettingsStore"/> ALSO
/// issues an idempotent <c>CREATE TABLE IF NOT EXISTS</c> so this table appears on an already-deployed host
/// <em>without</em> wiping the append-only audit log that shares the DB.
/// </para>
/// </summary>
public sealed class HostSettingsEntity
{
    /// <summary>This host's id (the primary key) — always <c>ApiOptions.HostId</c>. One row per host.</summary>
    public string Id { get; set; } = "";

    /// <summary>Display label override. Null ⇒ use the configured <c>KGSM_API_HOST_LABEL</c>.</summary>
    public string? Label { get; set; }

    /// <summary>Deployment region override — an arbitrary free string (e.g. <c>eu-west</c>). Null ⇒ use the
    /// configured <c>KGSM_API_REGION</c> (which is itself null when unset — honest unknown, never guessed).</summary>
    public string? Region { get; set; }

    /// <summary>When these overrides were last written (UTC). Null until first PATCH.</summary>
    public DateTimeOffset? UpdatedAt { get; set; }
}
