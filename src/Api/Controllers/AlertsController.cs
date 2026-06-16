using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Alerts;
using TheKrystalShip.Api.Services.Auth;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// The <c>/alerts</c> read surface (M6·a, architecture.html §3·c) — the condition-mirror: live problem
/// conditions now (<c>?status=firing</c>) and a short resolved rear-view (<c>?status=resolved&amp;since=24h</c>).
/// <b>Read-only by design</b>: there is no complete/dismiss/PATCH — the server raises and resolves; the
/// client never writes an alert. New conditions also arrive live on the <c>alerts</c> WS topic
/// (<c>alert.raise</c>/<c>resolve</c>/<c>retract</c>); this endpoint is the hydrate/backfill source (§3·j).
/// The durable, growing record of <em>what fired</em> lives in <c>/audit</c>, not here.
/// <para>
/// Gated at <b>viewer</b> (a core read surface, consistent with <c>/audit</c>). Served entirely from the
/// in-memory <see cref="AlertEngine"/> — alerts are never persisted (the rear-view ages off at 24h).
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/alerts")]
[Authorize(Policy = AuthPolicy.Viewer)]
public sealed partial class AlertsController(AlertEngine alerts) : ControllerBase
{
    /// <summary>
    /// <c>GET /alerts?status=firing|resolved&amp;since=24h</c> → <c>{ data }</c>. <c>status=firing</c> (the
    /// default) returns one record per live condition; <c>status=resolved</c> returns records that cleared
    /// within <c>since</c> (default + max 24h — the rear-view ages off). An unrecognized status falls back
    /// to firing (the needs-attention work surface).
    /// </summary>
    [HttpGet]
    public AlertPage GetAlerts([FromQuery] string? status, [FromQuery] string? since)
    {
        if (string.Equals(status, AlertStatus.Resolved, StringComparison.OrdinalIgnoreCase))
        {
            TimeSpan window = ParseSince(since) ?? TimeSpan.FromHours(24);
            return new AlertPage(alerts.ResolvedSince(DateTimeOffset.UtcNow - window));
        }

        return new AlertPage(alerts.Firing);
    }

    /// <summary>Parse a <c>since</c> duration (<c>"24h"</c>/<c>"30m"</c>/<c>"90s"</c>/<c>"2d"</c>) to a
    /// <see cref="TimeSpan"/>, clamped to the 24h retention (a longer window can't surface more — the
    /// rear-view ages off at 24h). Null on absent/garbage → the caller defaults to 24h.</summary>
    private static TimeSpan? ParseSince(string? since)
    {
        if (string.IsNullOrWhiteSpace(since)) return null;
        Match m = SincePattern().Match(since.Trim());
        if (!m.Success || !int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
            return null;

        TimeSpan span = m.Groups[2].Value switch
        {
            "s" => TimeSpan.FromSeconds(n),
            "m" => TimeSpan.FromMinutes(n),
            "h" => TimeSpan.FromHours(n),
            "d" => TimeSpan.FromDays(n),
            _ => TimeSpan.Zero,
        };
        TimeSpan max = TimeSpan.FromHours(24);
        return span <= TimeSpan.Zero ? null : (span > max ? max : span);
    }

    [GeneratedRegex(@"^(\d+)\s*([smhd])$", RegexOptions.IgnoreCase)]
    private static partial Regex SincePattern();
}
