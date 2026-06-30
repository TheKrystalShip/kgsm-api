namespace TheKrystalShip.Api.Data;

/// <summary>
/// One config <strong>override</strong> row for a leaf — a single <c>KEY=value</c> the API layers ON TOP of
/// the leaf's hand-deployed floor (the leaf-runtime-config feature, Phase 2). Composite-keyed by
/// (<see cref="LeafId"/>, <see cref="Key"/>) — one row per overridden manifest key per leaf. The
/// <see cref="Services.Leaves.LeafOverrideRenderer"/> materializes all of a leaf's rows into a deterministic
/// <c>&lt;LeafOverridesDir&gt;/&lt;leaf&gt;.env</c> file that a systemd drop-in feeds the leaf at start, so
/// the DB is the source of truth and the file is a pure render (reset = delete the row + re-render).
/// <para>
/// <b>Secret handling.</b> <see cref="IsSecret"/> rows hold a write-only secret (e.g. the assistant's web
/// search key): stored plaintext in the host-local SQLite (consistent with the env-stored bot token on this
/// single trusted host), <strong>never echoed on read</strong> (the API returns <c>set:true</c> + an optional
/// last-4 fingerprint), and never logged.
/// </para>
/// <para>
/// ⚠ <b>EnsureCreated, NOT a migration</b> — <see cref="Services.Leaves.LeafOverrideStore"/> issues an
/// idempotent <c>CREATE TABLE IF NOT EXISTS</c> so the table lands on an existing DB without a wipe.
/// </para>
/// </summary>
public sealed class LeafOverrideEntity
{
    /// <summary>The leaf id this override targets (<c>monitor</c>/<c>watchdog</c>/<c>assistant</c>/<c>firewall</c>).</summary>
    public string LeafId { get; set; } = "";

    /// <summary>The manifest field key (the stable id, e.g. <c>logLevel</c>) — NOT the env name.</summary>
    public string Key { get; set; } = "";

    /// <summary>The override value (already coerced to its canonical string form). For a secret this is the
    /// raw secret — never returned on read.</summary>
    public string? Value { get; set; }

    /// <summary>Whether this is a secret value (write-only, masked on read, never logged).</summary>
    public bool IsSecret { get; set; }

    /// <summary>When this override was last written (UTC).</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
