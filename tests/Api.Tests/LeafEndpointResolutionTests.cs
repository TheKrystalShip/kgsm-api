using TheKrystalShip.Api.Services.Leaves;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// How a host learns where its own leaves are. The answer has to hold on a node nobody configured,
/// which is the case that used to fail: every one of these leaves binds a fixed endpoint, and the API
/// shipped blank pointers to all of them, so a machine that installed the whole ecosystem came up with
/// the panel reporting five of them absent.
/// </summary>
public class LeafEndpointResolutionTests : IDisposable
{
    private readonly string _leaves = Path.Combine(Path.GetTempPath(), "kgsm-api-leafres-" + Guid.NewGuid().ToString("N"));

    public LeafEndpointResolutionTests() => Directory.CreateDirectory(_leaves);

    public void Dispose()
    {
        try { Directory.Delete(_leaves, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private void Install(params string[] leafIds)
    {
        foreach (string id in leafIds)
            File.WriteAllText(Path.Combine(_leaves, id + ".json"), "{}");
    }

    private ApiOptions Resolve(Action<ApiSettings>? configure = null)
    {
        var s = new ApiSettings
        {
            LeafDescriptorDir = _leaves,
            // Resolving the relay secret creates the file; keep it inside this test's own directory.
            RelaySecretPath = Path.Combine(_leaves, "relay-secret"),
        };
        configure?.Invoke(s);
        return ApiOptions.FromSettings(s);
    }

    [Fact]
    public void AnInstalledLeafIsWiredWithNothingConfigured()
    {
        Install("scheduler", "reactor", "bot", "firewall", "assistant");

        ApiOptions o = Resolve();

        Assert.Equal("/run/kgsm-scheduler/status.sock", o.SchedulerSocketPath);
        Assert.Equal("/run/kgsm-scheduler/control.sock", o.SchedulerControlSocketPath);
        Assert.Equal("/run/kgsm-reactor/status.sock", o.ReactorSocketPath);
        Assert.Equal("/run/kgsm-bot/status.sock", o.BotSocketPath);
        Assert.Equal("/run/kgsm-firewall/firewall.sock", o.FirewallSocketPath);
        Assert.Equal("http://127.0.0.1:5180", o.AssistantBaseUrl);

        Assert.True(o.SchedulerProvisioned);
        Assert.True(o.ReactorProvisioned);
        Assert.True(o.BotStatusProvisioned);
        Assert.True(o.FirewallProvisioned);
        Assert.True(o.AssistantProvisioned);
    }

    [Fact]
    public void AnUninstalledLeafIsAbsentRatherThanPerpetuallyDown()
    {
        // Nothing installed. A path resolved here would produce a leaf that is reported present and
        // permanently unreachable, which reads as a broken host rather than a host without that leaf.
        ApiOptions o = Resolve();

        Assert.Equal("", o.SchedulerSocketPath);
        Assert.Equal("", o.ReactorSocketPath);
        Assert.Equal("", o.BotSocketPath);
        Assert.Equal("", o.FirewallSocketPath);
        Assert.Equal("", o.AssistantBaseUrl);

        Assert.False(o.SchedulerProvisioned);
        Assert.False(o.ReactorProvisioned);
        Assert.False(o.BotStatusProvisioned);
        Assert.False(o.FirewallProvisioned);
        Assert.False(o.AssistantProvisioned);
    }

    [Fact]
    public void EachLeafIsResolvedOnItsOwn()
    {
        Install("scheduler");

        ApiOptions o = Resolve();

        Assert.Equal("/run/kgsm-scheduler/status.sock", o.SchedulerSocketPath);
        Assert.Equal("", o.ReactorSocketPath);
    }

    [Fact]
    public void APinnedPathWinsOverTheResolvedOne()
    {
        Install("reactor");

        ApiOptions o = Resolve(s => s.ReactorSocketPath = "/run/elsewhere/reactor.sock");

        Assert.Equal("/run/elsewhere/reactor.sock", o.ReactorSocketPath);
    }

    [Fact]
    public void AnExplicitlyBlankKeyKeepsAnInstalledLeafOffThePanel()
    {
        Install("bot", "scheduler");

        // The off switch survives: a host that does not want a leaf on its panel says so, and being
        // installed does not override the saying.
        ApiOptions o = Resolve(s => s.BotSocketPath = "");

        Assert.Equal("", o.BotSocketPath);
        Assert.False(o.BotStatusProvisioned);
        Assert.True(o.SchedulerProvisioned);
    }

    [Fact]
    public void AnUnreadableDescriptorDirectoryLeavesEveryLeafAbsent()
    {
        var s = new ApiSettings
        {
            LeafDescriptorDir = "/proc/self/mem/not-a-directory",
            RelaySecretPath = Path.Combine(_leaves, "relay-secret"),
        };

        ApiOptions o = ApiOptions.FromSettings(s);

        Assert.Equal("", o.SchedulerSocketPath);
        Assert.False(o.SchedulerProvisioned);
    }

    [Fact]
    public void TheEngineAndTheAlwaysOnLeavesAreNotGatedOnADescriptor()
    {
        // The monitor, the watchdog and the engine are not optional installs in the same sense: their
        // paths are the floor this API is built on, and a blank one is a misconfiguration rather than
        // a leaf that is not here.
        ApiOptions o = Resolve();

        Assert.Equal("/run/kgsm-monitor/metrics.sock", o.MonitorSocketPath);
        Assert.Equal("/run/kgsm-watchdog/control.sock", o.WatchdogSocketPath);
        Assert.Equal("/run/kgsm-speech/speech.sock", o.SpeechSocketPath);
        Assert.Equal("/usr/bin/kgsm", o.KgsmPath);
    }
}
