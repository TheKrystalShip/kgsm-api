namespace TheKrystalShip.Api.Services.Leaves;

/// <summary>
/// A leaf's config identity, resolved from its shipped descriptor where it has one and from this API's
/// static <see cref="LeafCatalog"/> where it does not.
/// </summary>
/// <param name="FromDescriptor">True when the leaf declared its own surface. False means only the keys this
/// API historically knew are exposed — the panel says so rather than implying the short list is everything.</param>
public sealed record LeafConfigIdentity(
    string Id,
    string DisplayName,
    string Unit,
    string ApplyMode,
    bool FromDescriptor,
    IReadOnlyList<LeafConfigGroup> Groups,
    LeafConfigDescriptor? Descriptor);

/// <summary>
/// Resolves what is configurable on this host: the union of the leaves that shipped a config descriptor and
/// the leaves this API carries a built-in manifest for.
/// </summary>
/// <remarks>
/// <para><b>The descriptor wins.</b> A leaf that ships one gets its full declared surface; the built-in
/// <see cref="LeafConfigManifest"/> is the fallback for a leaf that has not shipped one yet, so nothing
/// regresses while descriptors roll out repo by repo.</para>
/// <para><b>Readable and editable are separate questions.</b> Any descriptor makes a leaf's configuration
/// <em>visible</em> with full provenance. Editing additionally needs this host to have wired the delivery
/// channel for that leaf — the systemd drop-in that layers the API's override file, installed by kgsm-api's
/// <c>deploy/setup-leaf-config.sh</c>. Without it an apply would render a file nothing reads and then fail
/// at the restart, so the API reports the surface as locked, with the reason, instead of accepting a write
/// it cannot honour.</para>
/// </remarks>
public sealed class LeafConfigCatalog(LeafDescriptorStore descriptors, ApiOptions options)
{
    /// <summary>The drop-in kgsm-api's own setup installs per configurable leaf. Its presence is the
    /// checkable fact that this host can actually deliver an override to that unit.</summary>
    public const string OverrideDropInName = "50-kgsm-api-override.conf";

    /// <summary>True when this leaf has a config surface at all (readable; not necessarily editable).</summary>
    public bool IsConfigTarget(string? leafId) =>
        leafId is not null && (descriptors.For(leafId) is not null || LeafConfigManifest.IsConfigTarget(leafId));

    /// <summary>The settable fields for a leaf, or null when it has no config surface.</summary>
    public IReadOnlyList<LeafConfigFieldDef>? For(string leafId) =>
        descriptors.For(leafId)?.Fields ?? LeafConfigManifest.For(leafId);

    /// <summary>One field by key, or null when unknown.</summary>
    public LeafConfigFieldDef? Field(string leafId, string key) =>
        For(leafId)?.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.Ordinal));

    /// <summary>The leaf's config identity, or null when it has no config surface.</summary>
    public LeafConfigIdentity? Identity(string leafId)
    {
        LeafConfigDescriptor? d = descriptors.For(leafId);
        if (d is not null)
            return new LeafConfigIdentity(d.Id, d.DisplayName, d.Unit, d.ApplyMode, true, d.Groups, d);

        if (!LeafConfigManifest.IsConfigTarget(leafId))
            return null;

        LeafDescriptor? known = LeafCatalog.Default.FirstOrDefault(l => string.Equals(l.Id, leafId, StringComparison.Ordinal));
        return known is null
            ? null
            : new LeafConfigIdentity(known.Id, known.DisplayName, known.Unit, "restart", false, [], null);
    }

    /// <summary>
    /// Whether this host can actually deliver a config change to <paramref name="leafId"/>. Returns the
    /// reason when it cannot, phrased as the action that fixes it.
    /// </summary>
    public bool IsEditable(string leafId, out string? reason)
    {
        reason = null;
        LeafConfigIdentity? identity = Identity(leafId);
        if (identity is null)
        {
            reason = $"'{leafId}' has no configuration surface on this host.";
            return false;
        }

        string dropIn = Path.Combine(options.LeafDropInDir, identity.Unit + ".d", OverrideDropInName);
        if (File.Exists(dropIn))
            return true;

        reason = $"{identity.DisplayName} is not wired for configuration on this host — {identity.Unit} has no "
               + "kgsm-api override drop-in, so a change would be written but never read. Run kgsm-api's "
               + "deploy/setup-leaf-config.sh to wire it.";
        return false;
    }
}
