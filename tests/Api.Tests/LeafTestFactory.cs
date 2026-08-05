using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TheKrystalShip.Api.Services.Leaves;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// An <see cref="AuthTestFactory"/> with <strong>every leaf left unprovisioned</strong> (blank sockets/URL),
/// its own DB, and its own leaf-overrides dir — so the leaf-runtime-provisioning tests start from a clean
/// "all absent" baseline and a connect truly flips absent→provisioned. A fixed DB path can be supplied to
/// prove persistence survives a simulated restart (two factories, one DB).
/// </summary>
public class LeafTestFactory : AuthTestFactory
{
    private readonly string _dbPath;
    private readonly string _monitorSocket;
    public string OverridesDir { get; }

    public LeafTestFactory() : this(NewDbPath()) { }

    /// <summary>Per-factory descriptor + drop-in dirs. Both default to real host paths
    /// (<c>/var/lib/kgsm/leaves</c>, <c>/etc/systemd/system</c>), so leaving them alone would make these
    /// tests read whatever this machine happens to have deployed. Isolate them.</summary>
    public string DescriptorDir { get; }
    public string DropInDir { get; }

    /// <param name="monitorSocket">Non-blank to model a host whose config DOES provide the monitor — the
    /// only way to move a leaf's config seed between two factories sharing one DB.</param>
    public LeafTestFactory(string dbPath, string monitorSocket = "")
    {
        _dbPath = dbPath;
        _monitorSocket = monitorSocket;
        string id = Guid.NewGuid().ToString("N");
        OverridesDir = Path.Combine(Path.GetTempPath(), $"kgsm-api-leaf-ovr-{id}");
        DescriptorDir = Path.Combine(Path.GetTempPath(), $"kgsm-api-leaf-desc-{id}");
        DropInDir = Path.Combine(Path.GetTempPath(), $"kgsm-api-leaf-dropin-{id}");

        // The config-target leaves are "wired" by default here — the apply path refuses a leaf with no
        // override drop-in, and these tests are about the broker, not about host provisioning.
        foreach (LeafDescriptor leaf in LeafCatalog.Default)
        {
            Directory.CreateDirectory(Path.Combine(DropInDir, leaf.Unit + ".d"));
            File.WriteAllText(
                Path.Combine(DropInDir, leaf.Unit + ".d", LeafConfigCatalog.OverrideDropInName),
                "# test fixture\n");
        }
    }

    /// <summary>Install a descriptor for a leaf, as that leaf's own deploy would.</summary>
    public void InstallDescriptor(string leafId, string json)
    {
        Directory.CreateDirectory(DescriptorDir);
        File.WriteAllText(Path.Combine(DescriptorDir, leafId + ".json"), json);
    }

    /// <summary>Install a command manifest for a leaf, as that leaf's own deploy would — one directory below
    /// the descriptors, which is where the manifest scan looks and the descriptor scan does not.</summary>
    public void InstallCommands(string leafId, string json)
    {
        string dir = Path.Combine(DescriptorDir, "commands");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, leafId + ".json"), json);
    }

    /// <summary>Remove a leaf's override drop-in, modelling a host that never ran setup-leaf-config.sh.</summary>
    public void UnwireDropIn(string unit) =>
        File.Delete(Path.Combine(DropInDir, unit + ".d", LeafConfigCatalog.OverrideDropInName));

    private static string NewDbPath() => Path.Combine(Path.GetTempPath(), $"kgsm-api-leaf-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Api:DbPath"] = _dbPath,
                // All four leaves start NOT provisioned (blank) → connect flips absent→provisioned.
                ["Api:MonitorSocketPath"] = _monitorSocket,
                ["Api:WatchdogSocketPath"] = "",
                ["Api:AssistantBaseUrl"] = "",
                ["Api:FirewallSocketPath"] = "",
                ["Api:LeafOverridesDir"] = OverridesDir,
                ["Api:LeafDescriptorDir"] = DescriptorDir,
                ["Api:LeafDropInDir"] = DropInDir,
                // Keep the canary short so a rollback test doesn't wait 15s.
                ["Api:LeafApplyCanaryMs"] = "2000",
            }));
    }
}

/// <summary>
/// A <see cref="LeafTestFactory"/> that swaps the apply broker's two privileged/host seams for fakes — so the
/// config-apply tests exercise the real write→render→restart→canary→rollback flow with no systemd. The fake
/// probe is switch-on-input: a leaf is unhealthy iff one of its stored override values is the
/// <see cref="FakeLeafProbe.UnhealthyValue"/> sentinel (so a "bad value" deterministically triggers rollback).
/// </summary>
public sealed class LeafConfigTestFactory : LeafTestFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IUnitController>();
            services.AddSingleton<IUnitController, FakeUnitController>();
            services.RemoveAll<ILeafProbe>();
            services.AddSingleton<ILeafProbe, FakeLeafProbe>();
            services.RemoveAll<ILeafReachability>();
            services.AddSingleton<ILeafReachability, FakeLeafReachability>();
        });
    }

    public FakeUnitController Units() => (FakeUnitController)Services.GetRequiredService<IUnitController>();
}

/// <summary>Records each <c>RestartAsync(unit)</c> so a test can assert "restarted once" (apply) vs "twice"
/// (apply + rollback). Always reports the restart command itself succeeded.</summary>
public sealed class FakeUnitController : IUnitController
{
    private readonly ConcurrentDictionary<string, int> _restarts = new(StringComparer.Ordinal);

    public int RestartCount(string unit) => _restarts.TryGetValue(unit, out int n) ? n : 0;

    public Task<bool> RestartAsync(string unit, CancellationToken ct)
    {
        _restarts.AddOrUpdate(unit, 1, (_, n) => n + 1);
        return Task.FromResult(true);
    }
}

/// <summary>Switch-on-input canary: healthy unless a stored override value is the sentinel (a "bad value").
/// On rollback the snapshot is restored (the sentinel gone) so the post-rollback probe reports healthy.</summary>
public sealed class FakeLeafProbe(LeafOverrideStore store) : ILeafProbe
{
    public const string UnhealthyValue = "__make-unhealthy__";

    public async Task<bool> IsHealthyAsync(string leafId, CancellationToken ct)
    {
        IReadOnlyList<LeafOverrideRow> rows = await store.GetAsync(leafId, ct);
        return !rows.Any(r => r.Value == UnhealthyValue);
    }
}

/// <summary>
/// Switch-on-input reachability: the leaf is unreachable iff a stored override value is the sentinel. Models
/// the wiring break the liveness canary cannot see — the unit is perfectly healthy, this API just cannot
/// find it. Returns null (no signal) for the sentinel that models a leaf the API has no probe for.
/// </summary>
public sealed class FakeLeafReachability(LeafOverrideStore store) : ILeafReachability
{
    public const string UnreachableValue = "__make-unreachable__";
    public const string NoSignalValue = "__no-reachability-signal__";

    public async Task<bool?> IsReachableAsync(string leafId, CancellationToken ct)
    {
        IReadOnlyList<LeafOverrideRow> rows = await store.GetAsync(leafId, ct);
        if (rows.Any(r => r.Value == NoSignalValue)) return null;
        return !rows.Any(r => r.Value == UnreachableValue);
    }
}
