using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Services.Aggregation;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Metrics;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// Historical metrics endpoints (M9 Increment 3). Tier selection is automatic by range:
/// range &le; raw retention → raw (sample table, ~15s step); range &gt; raw retention → rollup
/// (rollup table, 5min step). Gaps are absent points. Unknown id → 404; history disabled or
/// monitor never seen → empty series (200, never a fabricated curve).
/// </summary>
[ApiController]
[Route("api/v1")]
[Authorize(Policy = AuthPolicy.Viewer)]
public sealed class MetricsHistoryController(
    MetricsHistoryStore store,
    ServerAggregator serverAggregator,
    ApiOptions options) : ControllerBase
{
    [HttpGet("servers/{id}/metrics/history")]
    public async Task<ActionResult<MetricsHistoryResponse>> GetServerHistory(
        string id, [FromQuery] string? range, CancellationToken ct)
    {
        if (!options.MetricsHistoryEnabled)
            return Ok(EmptyResponse(id, "server", range ?? MetricsRange.OneHour));

        Server? server = await serverAggregator.GetServerDetailAsync(id, ct);
        if (server is null)
            return NotFound();

        return Ok(await BuildResponseAsync("server", id, range, ct));
    }

    [HttpGet("hosts/{id}/metrics/history")]
    public async Task<ActionResult<MetricsHistoryResponse>> GetHostHistory(
        string id, [FromQuery] string? range, CancellationToken ct)
    {
        if (!options.MetricsHistoryEnabled)
            return Ok(EmptyResponse(id, "host", range ?? MetricsRange.OneHour));

        if (id != options.HostId)
            return NotFound();

        return Ok(await BuildResponseAsync("host", id, range, ct));
    }

    private async Task<MetricsHistoryResponse> BuildResponseAsync(
        string entityKind, string entityId, string? rangeStr, CancellationToken ct)
    {
        rangeStr ??= MetricsRange.OneHour;
        TimeSpan? duration = MetricsRange.Parse(rangeStr);
        if (duration is null)
            duration = TimeSpan.FromHours(1);

        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long fromMs = nowMs - (long)duration.Value.TotalMilliseconds;

        bool useRaw = duration.Value.TotalHours <= options.MetricsRawRetentionHours;

        if (useRaw)
        {
            List<MetricSample> samples = await store.ReadRawAsync(entityKind, entityId, null, fromMs, nowMs, ct);
            var series = new Dictionary<string, List<MetricsHistoryPoint>>();
            foreach (MetricSample s in samples)
            {
                if (!series.TryGetValue(s.Metric, out List<MetricsHistoryPoint>? list))
                {
                    list = [];
                    series[s.Metric] = list;
                }
                list.Add(new MetricsHistoryPoint(DateTimeOffset.FromUnixTimeMilliseconds(s.Ts), s.Value));
            }
            return new MetricsHistoryResponse(entityId, entityKind, rangeStr,
                options.MetricsPersistMs / 1000, "raw", series);
        }
        else
        {
            List<MetricRollup> rollups = await store.ReadRollupAsync(entityKind, entityId, null, fromMs, nowMs, ct);
            var series = new Dictionary<string, List<MetricsHistoryPoint>>();
            foreach (MetricRollup r in rollups)
            {
                if (!series.TryGetValue(r.Metric, out List<MetricsHistoryPoint>? list))
                {
                    list = [];
                    series[r.Metric] = list;
                }
                list.Add(new MetricsHistoryPoint(
                    DateTimeOffset.FromUnixTimeMilliseconds(r.BucketTs), r.Avg, r.Min, r.Max, r.N));
            }
            return new MetricsHistoryResponse(entityId, entityKind, rangeStr,
                options.MetricsRollupStepMin * 60, "rollup", series);
        }
    }

    private static MetricsHistoryResponse EmptyResponse(string entityId, string kind, string range) =>
        new(entityId, kind, range, 0, "raw", new Dictionary<string, List<MetricsHistoryPoint>>());
}
