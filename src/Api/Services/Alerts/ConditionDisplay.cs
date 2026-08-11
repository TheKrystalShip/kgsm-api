using System.Globalization;

namespace TheKrystalShip.Api.Services.Alerts;

/// <summary>
/// Renders a threshold condition's numbers into the words an alert card shows. This is the presentation
/// half of the metric-threshold source: the monitor decides <em>that</em> a value is over its line and
/// carries the raw number, and everything about how a person reads it is decided here.
/// </summary>
/// <remarks>
/// The unit is chosen from the monitor's metric name rather than sent over the wire, because a unit is a
/// property of the measurement and re-deriving it here keeps the leaf free of anything about rendering. An
/// unrecognised metric prints the bare number — a monitor that grows a metric this build has never heard of
/// still produces a readable alert, it just does not know how to label it.
/// </remarks>
public static class ConditionDisplay
{
    /// <summary>The measured value in its own unit, e.g. <c>94%</c>, <c>1.8 GiB</c>, <c>420 pids</c>.</summary>
    public static string Format(string metric, double value) => metric switch
    {
        "HostMemUsedPct" or "HostSwapUsedPct" or "HostDiskUsedPct" or "ServerCpuPctCore" => Pct(value),
        "HostLoadPerCore" => Load(value),
        "HostTempC" => Temp(value),
        "ServerMemBytes" => Bytes(value),
        "ServerPids" => Pids(value),
        _ => value.ToString("0.##", CultureInfo.InvariantCulture),
    };

    /// <summary>The noun the headline is built around ("memory at 94%"). Deliberately a plain word rather
    /// than the metric name — the card is read by somebody who does not know the daemon's vocabulary.</summary>
    public static string Noun(string metric) => metric switch
    {
        "HostMemUsedPct" => "memory",
        "HostSwapUsedPct" => "swap",
        "HostDiskUsedPct" => "disk",
        "HostLoadPerCore" => "load",
        "HostTempC" => "temperature",
        "ServerMemBytes" => "memory",
        "ServerCpuPctCore" => "CPU",
        "ServerPids" => "processes",
        _ => "metric",
    };

    /// <summary>A duration in the compact form the detail line uses (<c>30s</c>, <c>2m</c>, <c>1h 5m</c>).
    /// Whole minutes and hours print as such; anything else keeps its seconds, because rounding "held for
    /// 90s" to "1m" understates a dwell somebody chose deliberately.</summary>
    public static string Duration(long seconds)
    {
        if (seconds < 60) return $"{seconds}s";
        if (seconds < 3600)
            return seconds % 60 == 0 ? $"{seconds / 60}m" : $"{seconds / 60}m {seconds % 60}s";

        long hours = seconds / 3600;
        long minutes = seconds % 3600 / 60;
        return minutes == 0 ? $"{hours}h" : $"{hours}h {minutes}m";
    }

    private static string Pct(double value) => value.ToString("0", CultureInfo.InvariantCulture) + "%";

    private static string Load(double value) => value.ToString("0.0", CultureInfo.InvariantCulture) + "×/core";

    private static string Temp(double value) => value.ToString("0.0", CultureInfo.InvariantCulture) + "°C";

    private static string Pids(double value) => value.ToString("0", CultureInfo.InvariantCulture) + " pids";

    private static string Bytes(double bytes)
    {
        const double Gib = 1024.0 * 1024 * 1024;
        const double Mib = 1024.0 * 1024;
        return bytes >= Gib
            ? (bytes / Gib).ToString("0.0", CultureInfo.InvariantCulture) + " GiB"
            : (bytes / Mib).ToString("0.0", CultureInfo.InvariantCulture) + " MiB";
    }
}
