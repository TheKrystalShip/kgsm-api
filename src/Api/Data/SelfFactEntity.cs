namespace TheKrystalShip.Api.Data;

/// <summary>
/// One thing this node has learned about <em>itself</em> from a party that demonstrably reached it
/// (<c>PLAN-peers.md</c> §2 #13b, P0.6): an address it answers at, or a browser origin an admin signed in
/// from. A node cannot determine its own public address — behind NAT, a proxy or a load balancer the
/// address it is reached at leaves no local trace — so the facts here arrive by reflection and are what
/// let a node join a cluster with nothing configured but the shared secret.
/// <para>
/// <b>EnsureCreated, NOT a migration</b> (the project's dev authority — see <see cref="AppDbContext"/>):
/// because <c>EnsureCreated</c> no-ops on an existing DB,
/// <see cref="Services.Cluster.SelfIdentityStore"/> ALSO issues an idempotent
/// <c>CREATE TABLE IF NOT EXISTS</c>, so this table appears on an already-deployed host without wiping the
/// append-only audit log that shares the DB.
/// </para>
/// </summary>
public sealed class SelfFactEntity
{
    /// <summary>Primary key: <c><see cref="Kind"/>:<see cref="Value"/></c>, so recording the same fact
    /// twice refreshes one row instead of accumulating duplicates.</summary>
    public string Id { get; set; } = "";

    /// <summary><see cref="SelfFactKinds.Candidate"/> or <see cref="SelfFactKinds.Origin"/>.</summary>
    public string Kind { get; set; } = "";

    /// <summary>The address or origin itself, normalised (scheme and host lower-cased, no trailing
    /// slash).</summary>
    public string Value { get; set; } = "";

    /// <summary>For a candidate: whether a browser can use this address. Always true for a reflected
    /// candidate, since both reflection sources are browser-reachable by construction.</summary>
    public bool Client { get; set; }

    /// <summary>How this node came to believe the fact — <c>operator</c>, <c>observed</c>, or
    /// <c>peer-observed</c>. Orders candidate trust; never fabricated.</summary>
    public string Provenance { get; set; } = "";

    /// <summary>When the fact was last re-observed (UTC), so a stale address sorts below a live one.</summary>
    public DateTimeOffset LastSeen { get; set; }
}

/// <summary>The two <see cref="SelfFactEntity.Kind"/> values.</summary>
public static class SelfFactKinds
{
    /// <summary>An address this node answers at.</summary>
    public const string Candidate = "candidate";

    /// <summary>A browser origin an admin has signed in from.</summary>
    public const string Origin = "origin";
}
