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
    public string OverridesDir { get; }

    public LeafTestFactory() : this(NewDbPath()) { }

    public LeafTestFactory(string dbPath)
    {
        _dbPath = dbPath;
        OverridesDir = Path.Combine(Path.GetTempPath(), $"kgsm-api-leaf-ovr-{Guid.NewGuid():N}");
    }

    private static string NewDbPath() => Path.Combine(Path.GetTempPath(), $"kgsm-api-leaf-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["KGSM_API_DB"] = _dbPath,
                // All four leaves start NOT provisioned (blank) → connect flips absent→provisioned.
                ["KGSM_API_MONITOR_SOCKET"] = "",
                ["KGSM_API_WATCHDOG_SOCKET"] = "",
                ["KGSM_API_ASSISTANT_URL"] = "",
                ["KGSM_API_FIREWALL_SOCKET"] = "",
                ["KGSM_API_LEAF_OVERRIDES_DIR"] = OverridesDir,
                // Keep the canary short so a rollback test doesn't wait 15s.
                ["KGSM_API_LEAF_APPLY_CANARY_MS"] = "2000",
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
