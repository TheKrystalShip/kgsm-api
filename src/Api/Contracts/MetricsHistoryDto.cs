namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// The frozen response shape for <c>GET /servers/{id}/metrics/history</c> and
/// <c>GET /hosts/{id}/metrics/history</c> (M9 Increment 3). Tier selection is automatic:
/// range &le; raw retention → raw (15s step); range &gt; raw retention → rollup (5min step).
/// Gaps are absent points (sparse series, no carry-forward).
/// </summary>
public sealed record MetricsHistoryResponse(
    string EntityId,
    string Kind,
    string Range,
    int Step,
    string Tier,
    Dictionary<string, List<MetricsHistoryPoint>> Series);

/// <summary>A raw-tier point: just ts + value.</summary>
public sealed record MetricsHistoryPoint(
    DateTimeOffset Ts,
    double Value,
    double? Min = null,
    double? Max = null,
    int? N = null);

/// <summary>Known range strings and their durations.</summary>
public static class MetricsRange
{
    public const string OneHour = "1h";
    public const string TwentyFourHours = "24h";
    public const string SevenDays = "7d";
    public const string ThirtyDays = "30d";

    public static TimeSpan? Parse(string? range) => range switch
    {
        OneHour => TimeSpan.FromHours(1),
        TwentyFourHours => TimeSpan.FromHours(24),
        SevenDays => TimeSpan.FromDays(7),
        ThirtyDays => TimeSpan.FromDays(30),
        _ => null
    };
}
