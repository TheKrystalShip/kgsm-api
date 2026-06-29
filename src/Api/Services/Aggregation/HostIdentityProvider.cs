using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using TheKrystalShip.Api.Contracts;

namespace TheKrystalShip.Api.Services.Aggregation;

/// <summary>
/// Computes the <strong>runtime-derived, static</strong> half of this host's identity card — the OS the
/// API runs on, the .NET runtime, this build's version, and the API process start time. Every value is
/// honestly sourced (read from the OS / the assembly), never invented: a field that can't be read is
/// <see langword="null"/>, and the version is the assembly's real <see cref="AssemblyInformationalVersionAttribute"/>
/// (a <c>&lt;Version&gt;</c> + git SHA stamped at build), never a fabricated semver.
/// </summary>
/// <remarks>
/// Registered as a singleton: the OS/runtime/version are fixed for the process lifetime, so they are read
/// once (lazily) and cached — the same "static config read once" posture as <c>HostAggregator</c>'s install
/// directory. <see cref="StartedAt"/> is the real process start time (accurate whenever it is first read).
/// These are <em>API-process</em>/<em>host-OS</em> facts; the operator-declared region/label live in
/// <see cref="HostSettingsStore"/>, and host hardware capacity comes from the monitor.
/// </remarks>
public sealed class HostIdentityProvider
{
    private readonly Lazy<OsInfo> _os = new(ReadOs, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>OS name (distro pretty-name), kernel release, and architecture — each field honest-null when
    /// its source can't be read.</summary>
    public OsInfo Os => _os.Value;

    /// <summary>The .NET runtime this build runs on, e.g. <c>.NET 10.0.0</c> (<see cref="RuntimeInformation.FrameworkDescription"/>).</summary>
    public string Runtime { get; } = RuntimeInformation.FrameworkDescription;

    /// <summary>This build's version — the assembly informational version (<c>&lt;Version&gt;</c> + git SHA
    /// when present, e.g. <c>0.1.0+ab12cd34</c>). The single honest "which build is this host running" value.</summary>
    public string Build { get; } = ReadBuild();

    /// <summary>When this API process started (UTC) — distinct from host uptime; answers "did the API restart".</summary>
    public DateTimeOffset StartedAt { get; } = ReadProcessStart();

    private static OsInfo ReadOs() => new(ReadOsName(), ReadKernel(), ReadArch());

    // The distro's human name, e.g. "Arch Linux", from /etc/os-release PRETTY_NAME (the freedesktop
    // standard). Falls back to the runtime's OS description if the file is absent/unreadable; null only
    // if even that is blank. Never guessed.
    private static string? ReadOsName()
    {
        try
        {
            foreach (string line in File.ReadLines("/etc/os-release"))
            {
                if (!line.StartsWith("PRETTY_NAME=", StringComparison.Ordinal)) continue;
                string value = line["PRETTY_NAME=".Length..].Trim().Trim('"');
                if (value.Length > 0) return value;
            }
        }
        catch
        {
            // No /etc/os-release (non-Linux, container without it, permission) — fall through to the runtime.
        }

        string desc = RuntimeInformation.OSDescription.Trim();
        return desc.Length == 0 ? null : desc;
    }

    // The kernel release string, e.g. "7.0.12-arch1-1", from /proc/sys/kernel/osrelease (Linux). Null when
    // the file can't be read (non-Linux / restricted) — honest unknown, never a fabricated version.
    private static string? ReadKernel()
    {
        try
        {
            string value = File.ReadAllText("/proc/sys/kernel/osrelease").Trim();
            return value.Length == 0 ? null : value;
        }
        catch
        {
            return null;
        }
    }

    // The process architecture, e.g. "x64"/"arm64" (lowercased from RuntimeInformation). Always available.
    private static string ReadArch() => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();

    private static string ReadBuild()
    {
        Assembly asm = typeof(HostIdentityProvider).Assembly;
        string? info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info)) return info.Trim();
        // No informational version (shouldn't happen with <Version> set) — fall back to the plain version.
        return asm.GetName().Version?.ToString() ?? "unknown";
    }

    private static DateTimeOffset ReadProcessStart()
    {
        try
        {
            return Process.GetCurrentProcess().StartTime.ToUniversalTime();
        }
        catch
        {
            // Extremely defensive (StartTime can throw if the process exited) — fall back to "now".
            return DateTimeOffset.UtcNow;
        }
    }
}
