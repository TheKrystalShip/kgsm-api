using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using TheKrystalShip.Api.Contracts;

namespace TheKrystalShip.Api.Services.Logs;

/// <summary>
/// Reads the host's <b>systemd journal</b> and projects it into the aggregated, source-tagged
/// <see cref="LogPage"/> the <c>GET /hosts/{id}/logs</c> surface returns (architecture.html §3).
/// <para>
/// The aggregation is intrinsic to journald: a single <c>journalctl -u A -u B … --output=json</c> already
/// merges every configured leaf unit in chronological order, each entry tagged with its <c>_SYSTEMD_UNIT</c>
/// and carrying an opaque <c>__CURSOR</c> — which is exactly the keyset cursor architecture.html §6 asks for.
/// So this reader just shells journalctl (host-OS introspection, like the planned process table — NOT engine
/// data, so deliberately NOT routed through kgsm-lib), parses the NDJSON, and maps each entry to a
/// <see cref="LogLine"/>. Honesty: a journalctl failure (missing binary, no read access) degrades to an
/// empty page, never a fabricated line.
/// </para>
/// <para>
/// <b>Why <c>ArgumentList</c>, never a joined string.</b> Every argument is passed as its own
/// <see cref="ProcessStartInfo.ArgumentList"/> element — unit names and the cursor are config/opaque values
/// that must never be re-split on whitespace nor interpreted by a shell (the ecosystem ProcessRunner lesson).
/// User-supplied inputs (source, cursor, priority) are validated to a closed set/shape before they reach here.
/// </para>
/// </summary>
public sealed class JournalReader
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 500;

    private readonly ApiOptions _options;
    private readonly ILogger<JournalReader> _logger;
    private readonly IReadOnlyList<LogSourceMap> _sources;
    private readonly Dictionary<string, string> _unitToSource;

    public JournalReader(ApiOptions options, ILogger<JournalReader> logger)
    {
        _options = options;
        _logger = logger;
        _sources = options.LogSources;
        _unitToSource = _sources.ToDictionary(s => s.Unit, s => s.Source, StringComparer.Ordinal);
    }

    /// <summary>The ordered set of source ids this host can serve (the configured unit map), for the
    /// frontend's source dropdown and to validate a <c>?source=</c> filter.</summary>
    public IReadOnlyList<string> KnownSources => _sources.Select(s => s.Source).ToList();

    /// <summary>Is <paramref name="source"/> a configured source id?</summary>
    public bool IsKnownSource(string source) => _sources.Any(s => string.Equals(s.Source, source, StringComparison.Ordinal));

    /// <summary>The systemd units to follow/read, in configured order — for the live-tail bridge's
    /// <c>journalctl -f -u …</c> argument list.</summary>
    public IReadOnlyList<string> Units => _sources.Select(s => s.Unit).ToList();

    /// <summary>The <c>journalctl</c> binary + the read timeout, exposed for the live-tail bridge (it shells
    /// the same binary as the REST reader).</summary>
    public string JournalctlPath => _options.JournalctlPath;

    /// <summary>Parse one journald NDJSON line into a <see cref="LogLine"/> (merged-mode source derivation),
    /// or null if it isn't a usable entry. Shared with the live-tail bridge so the wire shape and the
    /// source/level mapping are identical to the REST page.</summary>
    public LogLine? ParseLine(string json) => ParseEntry(json, forcedSource: null);

    /// <summary>Clamp a client limit to <c>[1, <see cref="MaxLimit"/>]</c>, defaulting when unset.</summary>
    public static int ClampLimit(int? limit) =>
        limit is null || limit <= 0 ? DefaultLimit : Math.Min(limit.Value, MaxLimit);

    /// <summary>
    /// One keyset page, newest first. <paramref name="source"/> null ⇒ all configured units merged;
    /// otherwise that one unit (the caller validates it is known). <paramref name="cursor"/> is the
    /// previous page's <see cref="LogPage.NextCursor"/> (a journald cursor) — we seek to it and read the
    /// next <paramref name="limit"/> <em>older</em> entries (the cursor entry itself is excluded).
    /// <paramref name="priority"/> filters to a max syslog severity (<c>error|warn|info|debug</c> or 0–7).
    /// </summary>
    public async Task<LogPage> PageAsync(
        string? source, string? cursor, int limit, string? priority, CancellationToken ct)
    {
        IReadOnlyList<string> units = source is null
            ? _sources.Select(s => s.Unit).ToList()
            : _sources.Where(s => string.Equals(s.Source, source, StringComparison.Ordinal)).Select(s => s.Unit).ToList();

        if (units.Count == 0)
            return new LogPage([], null);

        // Reverse (newest-first) walk. We do NOT pass -n/--lines (it interacts badly with --cursor); instead
        // we read exactly as many NDJSON lines as we need off stdout and then stop the process. With --cursor
        // the walk *starts at* that entry (inclusive), so the first emitted line is the cursor itself — we
        // skip it so a page never repeats the previous page's last line.
        string? seekCursor = (cursor is not null && IsValidCursor(cursor)) ? cursor : null;

        var args = new List<string> { "--output=json", "--no-pager", "--reverse" };
        foreach (string unit in units) { args.Add("--unit"); args.Add(unit); }
        if (MapPriority(priority) is int p) { args.Add("--priority"); args.Add(p.ToString(CultureInfo.InvariantCulture)); }
        if (seekCursor is not null) { args.Add("--cursor"); args.Add(seekCursor); }

        var lines = new List<LogLine>(limit);
        try
        {
            var psi = new ProcessStartInfo(_options.JournalctlPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (string a in args) psi.ArgumentList.Add(a);

            using var proc = new Process { StartInfo = psi };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(_options.LogReadTimeoutMs));

            if (!proc.Start())
                return new LogPage([], null);

            try
            {
                string? raw;
                while ((raw = await proc.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false)) is not null)
                {
                    if (raw.Length == 0) continue;
                    // In single-source mode every returned line belongs to that source (they all matched
                    // `-u <unit>`, including systemd's own "Started/Stopped <unit>" lines) — force the tag so
                    // those lifecycle lines aren't mis-attributed to init.scope. Merged mode derives per-line.
                    LogLine? line = ParseEntry(raw, source);
                    if (line is null) continue;

                    // --cursor is inclusive: drop the seek entry itself (first line) so pages don't overlap.
                    if (seekCursor is not null && string.Equals(line.Id, seekCursor, StringComparison.Ordinal))
                        continue;

                    lines.Add(line);
                    if (lines.Count >= limit) break;
                }
            }
            finally
            {
                // We almost always stop reading before journalctl is done emitting (it would otherwise stream
                // the whole journal). Close stdout and kill the (now-SIGPIPE-bound) process so it never lingers.
                try { proc.StandardOutput.Close(); } catch { /* already gone */ }
                if (!proc.HasExited)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { /* race: exited */ }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the request was cancelled by the client — propagate, don't mask
        }
        catch (OperationCanceledException)
        {
            // our own read timeout fired — return what we gathered (honest partial), never a fabricated tail
            _logger.LogDebug("journalctl read timed out after {Ms}ms ({Got} lines)", _options.LogReadTimeoutMs, lines.Count);
        }
        catch (Exception ex)
        {
            // missing binary / no journal access / parse storm — degrade to an honest empty page (logged once-ish)
            _logger.LogWarning(ex, "journalctl read failed ({Path}); returning empty log page", _options.JournalctlPath);
            return new LogPage([], null);
        }

        // A full page ⇒ there may be older lines; a short page ⇒ we hit the end (no cursor).
        string? next = lines.Count >= limit && lines.Count > 0 ? lines[^1].Id : null;
        return new LogPage(lines, next);
    }

    /// <summary>Map one journald JSON entry to a <see cref="LogLine"/>, or null if it lacks a usable
    /// message/cursor (skipped, never fabricated). <paramref name="forcedSource"/> overrides the per-line
    /// source derivation (set in single-source mode — see the call site).</summary>
    private LogLine? ParseEntry(string json, string? forcedSource)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            string? id = GetString(root, "__CURSOR");
            if (string.IsNullOrEmpty(id)) return null;

            string text = GetMessage(root) ?? "";
            string sourceId = forcedSource ?? DeriveSource(root);

            DateTimeOffset at = ParseRealtime(GetString(root, "__REALTIME_TIMESTAMP"));
            string level = MapLevel(GetString(root, "PRIORITY"));

            return new LogLine(id, at, sourceId, level, text);
        }
        catch (JsonException)
        {
            return null; // a malformed line is dropped, not surfaced as a fake entry
        }
    }

    // Which leaf a (merged-mode) line belongs to. A service's own output carries _SYSTEMD_UNIT; systemd's
    // "Started/Stopped <unit>" messages about it carry _SYSTEMD_UNIT=init.scope but name the unit in UNIT=,
    // so we fall back to UNIT (and OBJECT_SYSTEMD_UNIT) before stripping. Unknown unit -> the bare name.
    private string DeriveSource(JsonElement root)
    {
        string own = GetString(root, "_SYSTEMD_UNIT") ?? "";
        if (_unitToSource.TryGetValue(own, out string? s)) return s;

        string about = GetString(root, "UNIT") ?? GetString(root, "OBJECT_SYSTEMD_UNIT") ?? "";
        if (about.Length > 0 && _unitToSource.TryGetValue(about, out string? s2)) return s2;

        return StripUnitSuffix(own.Length > 0 ? own : about);
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // MESSAGE is normally a string. journald emits it as an array of byte values when the original wasn't
    // valid UTF-8 — decode that best-effort rather than dropping the line.
    private static string? GetMessage(JsonElement root)
    {
        if (!root.TryGetProperty("MESSAGE", out JsonElement m)) return null;
        if (m.ValueKind == JsonValueKind.String) return m.GetString();
        if (m.ValueKind == JsonValueKind.Array)
        {
            var bytes = new List<byte>(m.GetArrayLength());
            foreach (JsonElement b in m.EnumerateArray())
                if (b.ValueKind == JsonValueKind.Number && b.TryGetInt32(out int n) && n is >= 0 and <= 255)
                    bytes.Add((byte)n);
            return Encoding.UTF8.GetString(bytes.ToArray());
        }
        return null;
    }

    // __REALTIME_TIMESTAMP is microseconds since the Unix epoch, as a string. Honest fallback: epoch.
    private static DateTimeOffset ParseRealtime(string? micros) =>
        long.TryParse(micros, NumberStyles.Integer, CultureInfo.InvariantCulture, out long us)
            ? DateTimeOffset.FromUnixTimeMilliseconds(us / 1000)
            : DateTimeOffset.UnixEpoch;

    // syslog priority 0..7 → the LogConsole's display level. Missing/garbage → info (never invented as error).
    private static string MapLevel(string? priority)
    {
        if (!int.TryParse(priority, NumberStyles.Integer, CultureInfo.InvariantCulture, out int p))
            return LogLineLevel.Info;
        return p switch
        {
            <= 3 => LogLineLevel.Error,
            4 => LogLineLevel.Warn,
            >= 7 => LogLineLevel.Debug,
            _ => LogLineLevel.Info,
        };
    }

    // A client ?priority= filter: a friendly name or a raw 0..7 → the journalctl -p max-severity number.
    // Anything else ⇒ no filter (null). Mirrors journalctl's "-p N shows priorities ≤ N".
    private static int? MapPriority(string? priority)
    {
        if (string.IsNullOrWhiteSpace(priority)) return null;
        string v = priority.Trim().ToLowerInvariant();
        return v switch
        {
            "error" or "err" => 3,
            "warn" or "warning" => 4,
            "info" => 6,
            "debug" => 7,
            _ when int.TryParse(v, out int n) && n is >= 0 and <= 7 => n,
            _ => null,
        };
    }

    private static string StripUnitSuffix(string unit) =>
        unit.EndsWith(".service", StringComparison.Ordinal) ? unit[..^".service".Length] : unit;

    // A journald cursor is a ';'-joined set of `key=value` fields (s=…;i=…;b=…;m=…;t=…;x=…). We pass it as a
    // single ArgumentList element (no shell), but still validate the shape so a value can't masquerade as a
    // flag (a leading '-') or smuggle anything unexpected. Closed character class: hex/letters, '=', ';'.
    private static bool IsValidCursor(string cursor) =>
        cursor.Length is > 0 and <= 1024
        && cursor[0] != '-'
        && cursor.All(c => char.IsAsciiLetterOrDigit(c) || c is '=' or ';');
}
