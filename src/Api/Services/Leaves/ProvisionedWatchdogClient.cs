using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Api.Services.Leaves;

/// <summary>
/// The watchdog client every consumer in this API resolves: the real control socket while the watchdog is
/// provisioned on this host, and nothing at all while it is not.
/// </summary>
/// <remarks>
/// <para>
/// <b>Provisioning is enforced here, once, rather than at each call site.</b> The client is registered
/// whether or not the watchdog is provisioned, so a runtime connect arms it without a restart. Leaving
/// every consumer to check the registry first is a rule a new consumer silently breaks — and several did:
/// on hotrod the test suite, which provisions no watchdog, reached the live daemon's socket from every
/// test host's background services and exhausted its file descriptors, and the daemon took two running
/// game servers down with it.
/// </para>
/// <para>
/// <b>Unprovisioned answers come from a real client that cannot connect</b>, pointed at a socket path
/// that does not exist. Every method then returns exactly what it returns for a watchdog that is down —
/// the answer each consumer already handles — with no second copy of those answers to keep in step with
/// the client's.
/// </para>
/// <para>
/// The registry is asked on every call, so connecting or disconnecting the watchdog at runtime takes
/// effect on the next request.
/// </para>
/// </remarks>
public sealed class ProvisionedWatchdogClient : IWatchdogClient
{
    /// <summary>
    /// Where the unprovisioned client points: inside a directory that cannot exist, so a connect fails at
    /// once rather than finding a socket somebody later created at an ordinary path.
    /// </summary>
    public const string UnprovisionedSocketPath = "/dev/null/kgsm-watchdog-unprovisioned.sock";

    private readonly Func<bool> _provisioned;
    private readonly IWatchdogClient _live;
    private readonly IWatchdogClient _absent;

    public ProvisionedWatchdogClient(Func<bool> provisioned, IWatchdogClient live, IWatchdogClient absent)
    {
        _provisioned = provisioned;
        _live = live;
        _absent = absent;
    }

    private IWatchdogClient Target => _provisioned() ? _live : _absent;

    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) =>
        Target.IsReadyAsync(cancellationToken);

    public Task<WatchdogReadyState?> GetReadyAsync(CancellationToken cancellationToken = default) =>
        Target.GetReadyAsync(cancellationToken);

    public Task<WatchdogActionResult> StartAsync(
        string instanceName, string origin = "scheduler", CancellationToken cancellationToken = default) =>
        Target.StartAsync(instanceName, origin, cancellationToken);

    public Task<WatchdogActionResult> StopAsync(
        string instanceName, string origin = "scheduler", CancellationToken cancellationToken = default) =>
        Target.StopAsync(instanceName, origin, cancellationToken);

    public Task<WatchdogActionResult> EnableAsync(string instanceName, CancellationToken cancellationToken = default) =>
        Target.EnableAsync(instanceName, cancellationToken);

    public Task<WatchdogActionResult> DisableAsync(string instanceName, CancellationToken cancellationToken = default) =>
        Target.DisableAsync(instanceName, cancellationToken);

    public Task<IReadOnlyList<string>> GetEnabledNamesAsync(CancellationToken cancellationToken = default) =>
        Target.GetEnabledNamesAsync(cancellationToken);

    public Task<WatchdogActionResult> ForgetAsync(string instanceName, CancellationToken cancellationToken = default) =>
        Target.ForgetAsync(instanceName, cancellationToken);

    public Task<WatchdogActionResult> SetCpuPriorityAsync(
        string instanceName, string priority, CancellationToken cancellationToken = default) =>
        Target.SetCpuPriorityAsync(instanceName, priority, cancellationToken);

    public Task<WatchdogActionResult> RestartAsync(
        string instanceName, string origin = "scheduler", CancellationToken cancellationToken = default) =>
        Target.RestartAsync(instanceName, origin, cancellationToken);

    public Task<WatchdogActionResult> BeginMaintenanceAsync(
        string instanceName, string origin = "scheduler", CancellationToken cancellationToken = default) =>
        Target.BeginMaintenanceAsync(instanceName, origin, cancellationToken);

    public Task<WatchdogActionResult> EndMaintenanceAsync(
        string instanceName, string origin = "scheduler", CancellationToken cancellationToken = default) =>
        Target.EndMaintenanceAsync(instanceName, origin, cancellationToken);

    public Task<WatchdogInstanceState?> GetStatusAsync(string instanceName, CancellationToken cancellationToken = default) =>
        Target.GetStatusAsync(instanceName, cancellationToken);

    public Task<IReadOnlyList<WatchdogInstanceState>> ListAsync(CancellationToken cancellationToken = default) =>
        Target.ListAsync(cancellationToken);

    public Task<IReadOnlyList<WatchdogRunTimes>> GetRunTimesAsync(CancellationToken cancellationToken = default) =>
        Target.GetRunTimesAsync(cancellationToken);

    public IAsyncEnumerable<string> FollowConsoleAsync(string instanceName, CancellationToken cancellationToken = default) =>
        Target.FollowConsoleAsync(instanceName, cancellationToken);

    public Task<IReadOnlyList<string>> GetConsoleTailAsync(
        string instanceName, int lines, CancellationToken cancellationToken = default) =>
        Target.GetConsoleTailAsync(instanceName, lines, cancellationToken);

    public Task<IReadOnlyList<WatchdogConsoleRun>> GetConsoleRunsAsync(
        string instanceName, CancellationToken cancellationToken = default) =>
        Target.GetConsoleRunsAsync(instanceName, cancellationToken);

    public Task<IReadOnlyList<string>> GetConsoleRunTailAsync(
        string instanceName, int lines, int run, CancellationToken cancellationToken = default) =>
        Target.GetConsoleRunTailAsync(instanceName, lines, run, cancellationToken);

    public Task<WatchdogConsoleWindow> GetConsoleWindowAsync(
        string instanceName, int lines, int run, long endOffset, CancellationToken cancellationToken = default) =>
        Target.GetConsoleWindowAsync(instanceName, lines, run, endOffset, cancellationToken);

    public Task<WatchdogConsoleDownload?> OpenConsoleDownloadAsync(
        string instanceName, int run, CancellationToken cancellationToken = default) =>
        Target.OpenConsoleDownloadAsync(instanceName, run, cancellationToken);

    public Task<IReadOnlyDictionary<string, WatchdogInstancePresence>?> GetPlayerPresenceAsync(
        CancellationToken cancellationToken = default) =>
        Target.GetPlayerPresenceAsync(cancellationToken);

    public Task<WatchdogUpnpList?> GetUpnpAsync(string instanceName, CancellationToken cancellationToken = default) =>
        Target.GetUpnpAsync(instanceName, cancellationToken);

    public void Dispose()
    {
        _live.Dispose();
        _absent.Dispose();
    }
}
