namespace TheKrystalShip.Api.Services.Leaves;

/// <summary>
/// The seam for the apply broker's <strong>health canary</strong> — "is this leaf healthy after the config
/// restart?" A test fake returns a canned verdict; the real impl reads the leaf's systemd liveness.
/// </summary>
public interface ILeafProbe
{
    /// <summary>Whether <paramref name="leafId"/>'s unit is healthy right now. The broker polls this after a
    /// restart until it goes true (good) or the canary window elapses (rollback).</summary>
    Task<bool> IsHealthyAsync(string leafId, CancellationToken ct);
}

/// <summary>
/// The real canary: a leaf is healthy iff its systemd unit came back up cleanly after the restart. We use
/// <strong>systemd liveness</strong> (via <see cref="SystemdReader"/>) rather than the §4·b <c>/health</c>
/// poll deliberately — it is registry-independent (the config target need not be SPA-"connected"), universal
/// (every leaf has a unit), and it is the ground truth for the primary failure mode this canary guards: a bad
/// override value makes the unit fail to start. A normal leaf must reach <c>active</c>; the on-demand,
/// socket-activated firewall (which idle-exits to <c>inactive</c>) is healthy as long as it is not
/// <c>failed</c>.
/// </summary>
public sealed class LeafProbe(SystemdReader systemd, ILogger<LeafProbe> logger) : ILeafProbe
{
    public async Task<bool> IsHealthyAsync(string leafId, CancellationToken ct)
    {
        LeafDescriptor? leaf = LeafCatalog.Default.FirstOrDefault(l => string.Equals(l.Id, leafId, StringComparison.Ordinal));
        if (leaf is null)
            return false;

        try
        {
            IReadOnlyDictionary<string, UnitState> states =
                await systemd.ReadAsync([leaf.Unit], ct).ConfigureAwait(false);
            UnitState st = states.TryGetValue(leaf.Unit, out UnitState? s) ? s : UnitState.Unknown;

            if (leaf.OnDemand)
                // Socket-activated + idle-exiting: active OR inactive are both "healthy at rest"; only a
                // failed/not-installed unit is unhealthy.
                return st.State is not ("failed" or "not-installed" or "unknown");

            // Normal leaf: it must be running. A transient `activating` resolves to active or failed as the
            // broker re-polls within the canary window.
            return string.Equals(st.State, "active", StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "leaf health probe failed for {Leaf}", leafId);
            return false;
        }
    }
}
