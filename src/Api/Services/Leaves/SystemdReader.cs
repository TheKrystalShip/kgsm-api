using System.Diagnostics;
using System.Globalization;

namespace TheKrystalShip.Api.Services.Leaves;

/// <summary>
/// The live state of one systemd unit, read via <c>systemctl show</c>. Every field is honest: an
/// unmeasured/unavailable value is <c>null</c>, and a unit the reader couldn't read at all is
/// <see cref="Unknown"/> — never a fabricated "running"/"stopped"/"0 bytes".
/// </summary>
/// <param name="State">The lifecycle state: systemd's <c>ActiveState</c> (<c>active|inactive|failed|
/// activating|deactivating|reloading|maintenance</c>), or <c>not-installed</c> (no such unit on the host),
/// <c>masked</c>, or <c>unknown</c> (read failed).</param>
/// <param name="SubState">systemd's finer <c>SubState</c> (<c>running|dead|exited|failed|…</c>), null if unknown.</param>
/// <param name="Enabled">Starts on boot? <c>true</c> for <c>enabled</c>, <c>false</c> for <c>disabled</c>,
/// <c>null</c> when enablement doesn't apply (<c>static</c>/<c>indirect</c>/masked) or is unknown.</param>
/// <param name="Since">When the unit last became active (<c>ActiveEnterTimestamp</c>), null if never/unknown —
/// the frontend derives uptime from it.</param>
/// <param name="MainPid">The main process pid, null when not running.</param>
/// <param name="MemoryBytes">systemd's cgroup memory accounting (<c>MemoryCurrent</c>), null when not running
/// or unavailable (<c>[not set]</c>) — measured, never invented.</param>
public sealed record UnitState(
    string State,
    string? SubState,
    bool? Enabled,
    DateTimeOffset? Since,
    int? MainPid,
    long? MemoryBytes)
{
    /// <summary>The unit couldn't be read (systemctl missing/errored/timed out) — liveness is honestly unknown.</summary>
    public static readonly UnitState Unknown = new("unknown", null, null, null, null, null);
}

/// <summary>
/// Reads each KGSM leaf's live systemd state for the Services board (<c>GET /hosts/{id}/services</c>) by
/// shelling <c>systemctl show</c>. This is host-OS introspection (the unit manager's own state) — sourced by
/// the api DIRECTLY, the same category as the host-log <see cref="Logs.JournalReader"/> and the file browser,
/// NOT through kgsm-lib (that chokepoint is for engine domain data; systemd unit state is not engine data).
/// <para>
/// One <c>systemctl show</c> call covers every unit: with multiple units it emits one <c>Key=Value</c> block
/// per unit, blank-line separated, each carrying <c>Id=</c> (so a not-found unit still gets a block with
/// <c>LoadState=not-found</c>). Timestamps are requested as <c>--timestamp=unix</c> (locale-free <c>@epoch</c>).
/// Reading unit state is unprivileged; any failure degrades every requested unit to <see cref="UnitState.Unknown"/>.
/// Arguments go through <see cref="ProcessStartInfo.ArgumentList"/> (never a joined string — the ProcessRunner lesson).
/// </para>
/// </summary>
public sealed class SystemdReader
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(4);

    // The properties we read per unit. Order is irrelevant (we index by name); each is a single-line scalar
    // (none can contain a newline), so blank-line block splitting is unambiguous.
    private static readonly string[] Properties =
        ["Id", "LoadState", "ActiveState", "SubState", "UnitFileState", "MainPID", "MemoryCurrent", "ActiveEnterTimestamp"];

    private readonly ApiOptions _options;
    private readonly ILogger<SystemdReader> _logger;

    public SystemdReader(ApiOptions options, ILogger<SystemdReader> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Read the live state of each requested unit. Returns a map keyed by unit name; every requested unit is
    /// present (a unit systemctl doesn't report, or a total failure, maps to <see cref="UnitState.Unknown"/> —
    /// never fabricated). The caller's <paramref name="ct"/> cancellation propagates; our own 4s budget does not.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, UnitState>> ReadAsync(IReadOnlyList<string> units, CancellationToken ct)
    {
        var result = new Dictionary<string, UnitState>(StringComparer.Ordinal);
        foreach (string u in units) result[u] = UnitState.Unknown;   // honest default for every requested unit
        if (units.Count == 0) return result;

        try
        {
            var psi = new ProcessStartInfo(_options.SystemctlPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("show");
            psi.ArgumentList.Add("--timestamp=unix");
            psi.ArgumentList.Add("--property=" + string.Join(',', Properties));
            foreach (string u in units) psi.ArgumentList.Add(u);

            using var proc = new Process { StartInfo = psi };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ReadTimeout);

            if (!proc.Start())
                return result;

            string stdout;
            try
            {
                stdout = await proc.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
                await proc.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            finally
            {
                if (!proc.HasExited)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { /* race: exited */ }
                }
            }

            MergeShowOutput(stdout, result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // client cancelled — propagate, don't mask
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("systemctl show timed out after {Timeout}; leaf liveness unknown", ReadTimeout);
        }
        catch (Exception ex)
        {
            // missing binary / no permission / parse storm — every unit stays Unknown (honest), never a fake state
            _logger.LogWarning(ex, "systemctl show failed ({Path}); leaf liveness unknown", _options.SystemctlPath);
        }

        return result;
    }

    /// <summary>
    /// Map the raw <c>systemctl show</c> multi-unit output onto <paramref name="into"/> (pre-seeded with every
    /// requested unit → <see cref="UnitState.Unknown"/>): each blank-line-separated block keyed by its
    /// <c>Id=</c> overwrites the matching entry; blocks for units we didn't ask for are ignored. Pure +
    /// deterministic (no process) so the parser is unit-testable.
    /// </summary>
    internal static void MergeShowOutput(string stdout, IDictionary<string, UnitState> into)
    {
        foreach (Dictionary<string, string> block in SplitBlocks(stdout))
        {
            if (!block.TryGetValue("Id", out string? id) || string.IsNullOrEmpty(id)) continue;
            if (!into.ContainsKey(id)) continue;   // only the units we asked for (defensive)
            into[id] = MapBlock(block);
        }
    }

    // Split `systemctl show` multi-unit output into per-unit Key=Value blocks (blank-line separated).
    private static IEnumerable<Dictionary<string, string>> SplitBlocks(string stdout)
    {
        var blocks = new List<Dictionary<string, string>>();
        var current = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string rawLine in stdout.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                if (current.Count > 0) { blocks.Add(current); current = new Dictionary<string, string>(StringComparer.Ordinal); }
                continue;
            }
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            current[line[..eq]] = line[(eq + 1)..];
        }
        if (current.Count > 0) blocks.Add(current);
        return blocks;
    }

    private static UnitState MapBlock(IReadOnlyDictionary<string, string> b)
    {
        string load = Get(b, "LoadState");
        string active = Get(b, "ActiveState");
        string sub = Get(b, "SubState");
        string unitFile = Get(b, "UnitFileState");

        // not-found (no such unit) and masked are load-time facts that override the active state.
        string state =
            load == "not-found" ? "not-installed" :
            load == "masked" ? "masked" :
            active.Length > 0 ? active :
            "unknown";

        bool? enabled = unitFile switch
        {
            "enabled" or "enabled-runtime" => true,
            "disabled" => false,
            _ => null,   // static / indirect / generated / transient / masked / "" — enablement N/A or unknown
        };

        return new UnitState(
            State: state,
            SubState: sub.Length > 0 ? sub : null,
            Enabled: enabled,
            Since: ParseUnixStamp(Get(b, "ActiveEnterTimestamp")),
            MainPid: ParsePid(Get(b, "MainPID")),
            MemoryBytes: ParseMemory(Get(b, "MemoryCurrent")));
    }

    private static string Get(IReadOnlyDictionary<string, string> b, string key) =>
        b.TryGetValue(key, out string? v) ? v : "";

    // --timestamp=unix renders as "@<epoch-seconds>" (empty when never active). Take the integer seconds.
    private static DateTimeOffset? ParseUnixStamp(string v)
    {
        if (string.IsNullOrEmpty(v)) return null;
        string s = v[0] == '@' ? v[1..] : v;
        int dot = s.IndexOf('.');
        if (dot >= 0) s = s[..dot];
        return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long secs) && secs > 0
            ? DateTimeOffset.FromUnixTimeSeconds(secs)
            : null;
    }

    // MainPID=0 means "no main process" → null (not running), never a fake pid.
    private static int? ParsePid(string v) =>
        int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0 ? n : null;

    // MemoryCurrent is a byte count when running; "[not set]" (idle) or the uint64-max sentinel → null
    // (both fail the long parse), so an idle/unavailable unit reports null memory, never a fabricated 0.
    private static long? ParseMemory(string v) =>
        long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) && n >= 0 ? n : null;
}
