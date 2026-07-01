using System.Globalization;
using TheKrystalShip.KGSM.Monitor.Contracts;

namespace TheKrystalShip.Api.Services.Alerts;

/// <summary>
/// The closed set of monitor <see cref="Snapshot"/> fields a threshold rule may watch (the
/// metric-threshold alerts source, increment 1 of 2 — <c>metrics-threshold-alerts-plan.md</c>). Closed
/// deliberately: thresholding a field this enum doesn't name is a compile error, never a runtime guess at
/// what the monitor honestly carries. <c>Host*</c> members are host-scope (the resulting
/// <see cref="MetricObservation.ServerId"/> is always null); <c>Server*</c> members are per-server (one
/// observation per <see cref="Snapshot.Servers"/> row) — see <see cref="ThresholdMetrics.IsHostScope"/>.
/// </summary>
public enum ThresholdMetric
{
    /// <summary>Host RAM, percent (<c>Snapshot.Mem.UsedPct</c>).</summary>
    HostMemUsedPct,

    /// <summary>Host swap, percent (<c>100 * Mem.SwapUsedKb / Mem.SwapTotalKb</c>) — not evaluable
    /// (no swap configured on this host) when <c>SwapTotalKb == 0</c>.</summary>
    HostSwapUsedPct,

    /// <summary>Per-mount disk usage, percent (<c>Snapshot.Disk.Mounts[].UsedPct</c>) — fans out: one
    /// observation per mount, <see cref="MetricObservation.RefKey"/> = the mount path.</summary>
    HostDiskUsedPct,

    /// <summary>5-minute load average per core (<c>Cpu.Load.Five / Cpu.Info.Cores</c>) — not evaluable
    /// when <c>Snapshot.Cpu.Info</c> (or its <c>Cores</c>) is null.</summary>
    HostLoadPerCore,

    /// <summary>hwmon sensor temperature, °C (<c>Snapshot.Sensors[].ValueC</c>) — fans out: one
    /// observation per sensor (none when the array is empty), <see cref="MetricObservation.RefKey"/> =
    /// the chip/label.</summary>
    HostTempC,

    /// <summary>Per-server resident memory, bytes (<c>ServerMetrics.MemBytes</c>) — one observation per
    /// <see cref="Snapshot.Servers"/> row.</summary>
    ServerMemBytes,

    /// <summary>Per-server CPU, percent of ONE core (<c>ServerMetrics.CpuPctCore</c>) — can exceed 100
    /// on a multi-threaded server; one observation per <see cref="Snapshot.Servers"/> row.</summary>
    ServerCpuPctCore,

    /// <summary>Per-server live process/thread count (<c>ServerMetrics.Pids</c>) — one observation per
    /// <see cref="Snapshot.Servers"/> row.</summary>
    ServerPids,
}

/// <summary>
/// One ">=" comparison against one <see cref="ThresholdMetric"/> ("too high" is the only direction the
/// default policy needs). Config-bindable (<c>MetricsThresholds:Rules</c> in <c>appsettings.json</c>) —
/// see <see cref="MetricsThresholdPolicy"/>.
/// </summary>
/// <param name="Key">Stable rule key used in the alert id (e.g. <c>"host-disk"</c>, <c>"srv-pids"</c>) —
/// see <see cref="ThresholdMetrics.AlertId"/>.</param>
/// <param name="Metric">Which snapshot field this rule watches.</param>
/// <param name="Warn">Value &gt;= <see cref="Warn"/> fires the <c>warn</c> severity band.</param>
/// <param name="Danger">Value &gt;= <see cref="Danger"/> fires the <c>danger</c> band (a re-push of the
/// same alert id); <see langword="null"/> = warn-only, this rule never escalates to danger.</param>
/// <param name="FireForSec">Dwell-to-fire, in seconds: the value must stay at/above <see cref="Warn"/>
/// this long before the alert raises — kills a one-tick spike.</param>
/// <param name="ClearForSec">Dwell-to-clear, in seconds: the value must stay below the clear threshold
/// (<see cref="Warn"/> − <see cref="ClearMargin"/>) this long before a firing alert resolves.</param>
/// <param name="ClearMargin">Hysteresis deadband: the value must drop this far below <see cref="Warn"/>
/// before the clear dwell even starts — a value hovering right at <see cref="Warn"/> never flaps.</param>
/// <param name="Enabled">Whether this rule is active. Per the default policy, host rules ship
/// <see langword="true"/> (universal, percent-based) and per-server rules ship <see langword="false"/>
/// (absolute thresholds are game-specific — operator opt-in via config).</param>
public sealed record ThresholdRule(
    string Key,
    ThresholdMetric Metric,
    double Warn,
    double? Danger,
    int FireForSec,
    int ClearForSec,
    double ClearMargin,
    bool Enabled);

/// <summary>
/// The metric-threshold alert policy — increment 1's storage model is appsettings/env only (a baked-in
/// <see cref="Default"/>, optionally wholesale-overridden by the <c>MetricsThresholds:Rules</c> config
/// section; see <c>ApiOptions.Policy</c>). DB-backed, panel-editable policy is increment 2 — out of scope
/// here. Tuning a threshold or enabling a per-server rule means editing config and restarting the API.
/// </summary>
/// <param name="Rules">The configured rule set, in no particular order. A disabled rule is kept (not
/// dropped) so it can be inspected/toggled, but contributes no observations.</param>
public sealed record MetricsThresholdPolicy(IReadOnlyList<ThresholdRule> Rules)
{
    /// <summary>Whether at least one rule is enabled — the metric-threshold source has anything to do.
    /// See <c>ApiOptions.MetricsThresholdProvisioned</c>.</summary>
    public bool AnyEnabled => Rules.Any(r => r.Enabled);

    /// <summary>
    /// The baked-in default policy (see <c>metrics-threshold-alerts-plan.md</c> "Default policy"). Host
    /// rules (universal, percent-based) ship <c>enabled:true</c>; per-server rules (absolute, game-specific
    /// thresholds) ship <c>enabled:false</c> with placeholder <c>warn:0</c> — inert until an operator opts
    /// in and tunes them. <c>host-disk</c> leads: a full disk actually crashes servers, the single most
    /// valuable alert here.
    /// </summary>
    public static readonly MetricsThresholdPolicy Default = new(new List<ThresholdRule>
    {
        new(Key: "host-disk", Metric: ThresholdMetric.HostDiskUsedPct, Warn: 90, Danger: 95,
            FireForSec: 60, ClearForSec: 300, ClearMargin: 3, Enabled: true),
        new(Key: "host-mem", Metric: ThresholdMetric.HostMemUsedPct, Warn: 90, Danger: 97,
            FireForSec: 120, ClearForSec: 120, ClearMargin: 5, Enabled: true),
        new(Key: "host-swap", Metric: ThresholdMetric.HostSwapUsedPct, Warn: 50, Danger: 90,
            FireForSec: 120, ClearForSec: 120, ClearMargin: 10, Enabled: true),
        new(Key: "host-load", Metric: ThresholdMetric.HostLoadPerCore, Warn: 1.5, Danger: 4.0,
            FireForSec: 120, ClearForSec: 120, ClearMargin: 0.3, Enabled: true),
        new(Key: "host-temp", Metric: ThresholdMetric.HostTempC, Warn: 85, Danger: 95,
            FireForSec: 30, ClearForSec: 60, ClearMargin: 5, Enabled: true),
        new(Key: "srv-pids", Metric: ThresholdMetric.ServerPids, Warn: 1000, Danger: null,
            FireForSec: 120, ClearForSec: 120, ClearMargin: 50, Enabled: false),
        new(Key: "srv-mem", Metric: ThresholdMetric.ServerMemBytes, Warn: 0, Danger: null,
            FireForSec: 120, ClearForSec: 120, ClearMargin: 0, Enabled: false),
        new(Key: "srv-cpu", Metric: ThresholdMetric.ServerCpuPctCore, Warn: 0, Danger: null,
            FireForSec: 120, ClearForSec: 120, ClearMargin: 0, Enabled: false),
    });
}

/// <summary>
/// Mutable binding shape for one rule under <c>MetricsThresholds:Rules</c>. Bound via settable properties
/// (NOT the positional <see cref="ThresholdRule"/> ctor): the configuration binder cannot construct a
/// record whose <c>double? Danger</c> ctor parameter has no config value, and silently yields an empty list
/// for the whole section — so a single warn-only rule (<c>danger</c> absent/null) would drop the operator's
/// entire custom policy back to <see cref="MetricsThresholdPolicy.Default"/>. Settable nullable properties
/// bind a missing/null <c>danger</c> cleanly. See <c>ApiOptions.LoadThresholdPolicy</c>.
/// </summary>
internal sealed class ThresholdRuleBinding
{
    public string? Key { get; set; }
    public ThresholdMetric Metric { get; set; }
    public double Warn { get; set; }
    public double? Danger { get; set; }
    public int FireForSec { get; set; }
    public int ClearForSec { get; set; }
    public double ClearMargin { get; set; }
    public bool Enabled { get; set; }

    public ThresholdRule ToRule() =>
        new(Key!, Metric, Warn, Danger, FireForSec, ClearForSec, ClearMargin, Enabled);
}

/// <summary>
/// One measured target a <see cref="ThresholdRule"/> can be compared against, yielded by
/// <see cref="ThresholdMetrics.Observe"/>. A host-scope rule yields one observation per evaluable target
/// (a singleton for most host metrics, one per mount/sensor for the fan-out ones); a server-scope rule
/// yields one per reporting <see cref="Snapshot.Servers"/> row. A target that isn't evaluable this tick
/// (a null field, no swap, no cpu-info, no sensors) yields nothing — never a fabricated value.
/// </summary>
/// <param name="Id">The full alert id (<see cref="ThresholdMetrics.AlertId"/>), e.g.
/// <c>"metric:host-disk:/"</c> or <c>"metric:srv-pids:factorio-test"</c>.</param>
/// <param name="RefKey">The mount path / sensor chip-label, or <see langword="null"/> for a singleton
/// host metric and for server-scope metrics (which are keyed by <see cref="ServerId"/> instead).</param>
/// <param name="ServerId">The reporting server's instance id for a server-scope metric, or
/// <see langword="null"/> for a host-scope metric.</param>
/// <param name="Value">The measured value, compared against <see cref="ThresholdRule.Warn"/>/
/// <see cref="ThresholdRule.Danger"/>.</param>
/// <param name="Display">Unit-aware rendering of <see cref="Value"/>, e.g. <c>"94%"</c>,
/// <c>"1.8 GiB"</c>, <c>"420 pids"</c>, <c>"1.7×/core"</c>.</param>
public readonly record struct MetricObservation(
    string Id,
    string? RefKey,
    string? ServerId,
    double Value,
    string Display);

/// <summary>
/// Centralizes the monitor-<see cref="Snapshot"/>-field knowledge for the metric-threshold alerts source,
/// the way <c>Services/Aggregation/MetricsMapping.cs</c> centralizes it for the REST/WS metrics surfaces.
/// The alert engine never reads a <see cref="Snapshot"/> field directly — it calls <see cref="Observe"/>
/// and works only with <see cref="MetricObservation"/>.
/// </summary>
public static class ThresholdMetrics
{
    /// <summary>Whether <paramref name="metric"/> is a host-scope metric (singleton or fan-out over
    /// mounts/sensors, <c>ServerId</c> always null) as opposed to a per-server one. Scope is derived from
    /// the metric — not stored redundantly on the rule — so there is exactly one source of truth.</summary>
    public static bool IsHostScope(ThresholdMetric metric) => metric switch
    {
        ThresholdMetric.HostMemUsedPct => true,
        ThresholdMetric.HostSwapUsedPct => true,
        ThresholdMetric.HostDiskUsedPct => true,
        ThresholdMetric.HostLoadPerCore => true,
        ThresholdMetric.HostTempC => true,
        _ => false,
    };

    /// <summary>
    /// Yields one <see cref="MetricObservation"/> per evaluable target of <paramref name="rule"/> against
    /// <paramref name="snap"/>. Skips — never throws on — a target that isn't honestly evaluable this
    /// tick: a null field, a null <c>Cpu.Info</c>, <c>SwapTotalKb == 0</c>, or an empty
    /// <see cref="Snapshot.Sensors"/>/<see cref="Snapshot.Servers"/> array. The caller is responsible for
    /// only invoking this with a non-null <paramref name="snap"/> and an enabled <paramref name="rule"/>
    /// (a down monitor / disabled rule is the caller's honest-unknown / no-op to handle, not this method's).
    /// </summary>
    public static IEnumerable<MetricObservation> Observe(ThresholdRule rule, Snapshot snap)
    {
        switch (rule.Metric)
        {
            case ThresholdMetric.HostMemUsedPct:
            {
                double value = snap.Mem.UsedPct;
                yield return Host(rule.Key, value, FormatPct(value));
                break;
            }

            case ThresholdMetric.HostSwapUsedPct:
            {
                if (snap.Mem.SwapTotalKb == 0) yield break; // not evaluable: no swap configured
                double value = 100.0 * snap.Mem.SwapUsedKb / snap.Mem.SwapTotalKb;
                yield return Host(rule.Key, value, FormatPct(value));
                break;
            }

            case ThresholdMetric.HostDiskUsedPct:
            {
                foreach (MountUsage mount in snap.Disk?.Mounts ?? [])
                    yield return new MetricObservation(
                        AlertId(rule.Key, mount.Mount), mount.Mount, ServerId: null,
                        mount.UsedPct, FormatPct(mount.UsedPct));
                break;
            }

            case ThresholdMetric.HostLoadPerCore:
            {
                int? cores = snap.Cpu?.Info?.Cores;
                if (cores is null or 0) yield break; // not evaluable: no cpu-info / unknown core count
                double value = snap.Cpu!.Load.Five / cores.Value;
                yield return Host(rule.Key, value, FormatLoad(value));
                break;
            }

            case ThresholdMetric.HostTempC:
            {
                foreach (SensorReading sensor in snap.Sensors ?? [])
                {
                    string refKey = string.IsNullOrEmpty(sensor.Label)
                        ? sensor.Chip
                        : $"{sensor.Chip}/{sensor.Label}";
                    yield return new MetricObservation(
                        AlertId(rule.Key, refKey), refKey, ServerId: null,
                        sensor.ValueC, FormatTemp(sensor.ValueC));
                }
                break;
            }

            case ThresholdMetric.ServerMemBytes:
            {
                foreach (ServerMetrics srv in snap.Servers ?? [])
                    yield return Server(rule.Key, srv.Id, srv.MemBytes, FormatBytes(srv.MemBytes));
                break;
            }

            case ThresholdMetric.ServerCpuPctCore:
            {
                foreach (ServerMetrics srv in snap.Servers ?? [])
                    yield return Server(rule.Key, srv.Id, srv.CpuPctCore, FormatPct(srv.CpuPctCore));
                break;
            }

            case ThresholdMetric.ServerPids:
            {
                foreach (ServerMetrics srv in snap.Servers ?? [])
                    yield return Server(rule.Key, srv.Id, srv.Pids, FormatPids(srv.Pids));
                break;
            }

            default:
                yield break; // closed enum — an unmapped member is a no-op, never a guess
        }
    }

    /// <summary>The alert id: <c>"metric:&lt;key&gt;"</c> for a singleton host metric, or
    /// <c>"metric:&lt;key&gt;:&lt;ref-or-serverId&gt;"</c> for a fan-out / per-server one. Stable across
    /// ticks for the same target — a re-fire upserts the same record (the engine's raise/re-push
    /// semantics), never a fresh per-raise id.</summary>
    public static string AlertId(string ruleKey, string? refOrServerId) =>
        string.IsNullOrEmpty(refOrServerId) ? $"metric:{ruleKey}" : $"metric:{ruleKey}:{refOrServerId}";

    private static MetricObservation Host(string ruleKey, double value, string display) =>
        new(AlertId(ruleKey, null), RefKey: null, ServerId: null, value, display);

    private static MetricObservation Server(string ruleKey, string serverId, double value, string display) =>
        new(AlertId(ruleKey, serverId), RefKey: null, serverId, value, display);

    // --- Display formatting: simple and honest, no false precision. -----------------------------------

    private static string FormatPct(double value) =>
        value.ToString("0", CultureInfo.InvariantCulture) + "%";

    private static string FormatLoad(double value) =>
        value.ToString("0.0", CultureInfo.InvariantCulture) + "×/core";

    private static string FormatTemp(double value) =>
        value.ToString("0.0", CultureInfo.InvariantCulture) + "°C";

    private static string FormatPids(double value) =>
        value.ToString("0", CultureInfo.InvariantCulture) + " pids";

    private static string FormatBytes(double bytes)
    {
        const double Gib = 1024.0 * 1024 * 1024;
        const double Mib = 1024.0 * 1024;
        return bytes >= Gib
            ? (bytes / Gib).ToString("0.0", CultureInfo.InvariantCulture) + " GiB"
            : (bytes / Mib).ToString("0.0", CultureInfo.InvariantCulture) + " MiB";
    }
}
