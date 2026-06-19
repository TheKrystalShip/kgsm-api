namespace TheKrystalShip.Api.Data;

/// <summary>
/// One outbound-notification integration's stored config (M8·c, architecture.html §3·e), keyed by
/// <see cref="Provider"/> (one row per provider — <c>discord</c> first, <c>slack</c>/<c>telegram</c>
/// later). This is the API's first non-audit persisted entity; it lives in the same
/// <see cref="AppDbContext"/>, so the existing <c>EnsureCreated</c> at startup creates it too.
/// <para>
/// ⚠ <b>EnsureCreated, NOT a migration</b> (the project's dev authority — see <see cref="AppDbContext"/>):
/// because <c>EnsureCreated</c> no-ops on an existing DB, adding this table means the dev DB file must be
/// deleted once. Smoke <c>rm -f</c>s its own DB each run; tests use a fresh temp DB.
/// </para>
/// <para>
/// <b>Secret handling:</b> <see cref="Secret"/> (the provider's webhook URL) is stored plaintext in the
/// host-local SQLite — consistent with the env-stored bot token / client secret on this single trusted host.
/// It is <b>masked on read</b> (the API returns a hint, never the URL) and <b>write-only on PATCH</b>.
/// </para>
/// </summary>
public sealed class IntegrationEntity
{
    /// <summary>The provider id (<c>discord</c>/…) — the primary key. One config row per provider.</summary>
    public string Provider { get; set; } = "";

    /// <summary>Whether the integration is on (the delivery worker, M8·c Increment B, honors this).</summary>
    public bool Enabled { get; set; }

    /// <summary>The one secret — the provider's webhook URL. Null = not configured. Never echoed on read.</summary>
    public string? Secret { get; set; }

    /// <summary>A cosmetic channel label (e.g. <c>#krystal-ops</c>) — the provider owns the real channel.</summary>
    public string? ChannelLabel { get; set; }

    /// <summary>Provider-specific settings as a JSON object string (e.g. a discord ops-role), or null.
    /// Generic so the column doesn't grow a provider-specific shape; the provider interprets its keys.</summary>
    public string? Settings { get; set; }

    /// <summary>The per-event routing rules as a JSON array string (<c>[{id,enabled,cadence,ping}]</c>),
    /// overlaid on the server-defined catalog on read. Null/empty = every event at its default.</summary>
    public string? Events { get; set; }

    /// <summary>When the config was last written (UTC).</summary>
    public DateTimeOffset? UpdatedAt { get; set; }
}
