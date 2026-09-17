using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.Api.Services.Leaves;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Services;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// An unprovisioned watchdog is never dialed, whichever consumer asks.
/// </summary>
/// <remarks>
/// Real clients over a real unix socket that counts who connects, because what matters is whether a
/// connection is made at all — the failure this prevents was file-descriptor exhaustion on a live daemon,
/// which no fake that records method calls can show.
/// </remarks>
public sealed class ProvisionedWatchdogClientTests : IDisposable
{
    private readonly string _socketPath = Path.Combine(Path.GetTempPath(), $"wd-{Guid.NewGuid():N}"[..12] + ".sock");
    private readonly Socket _listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    private int _accepted;

    public ProvisionedWatchdogClientTests()
    {
        _listener.Bind(new UnixDomainSocketEndPoint(_socketPath));
        _listener.Listen(16);
        _ = AcceptLoopAsync();
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (true)
            {
                Socket peer = await _listener.AcceptAsync().ConfigureAwait(false);
                Interlocked.Increment(ref _accepted);
                peer.Dispose(); // no HTTP answer: the call fails, and the connection has been counted
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SocketException)
        {
            // listener closed
        }
    }

    public void Dispose()
    {
        _listener.Dispose();
        File.Delete(_socketPath);
    }

    private ProvisionedWatchdogClient Client(Func<bool> provisioned) => new(
        provisioned,
        new WatchdogClient(new WatchdogClientOptions { SocketPath = _socketPath }, NullLogger<WatchdogClient>.Instance),
        new WatchdogClient(
            new WatchdogClientOptions { SocketPath = ProvisionedWatchdogClient.UnprovisionedSocketPath },
            NullLogger<WatchdogClient>.Instance));

    [Fact]
    public async Task Unprovisioned_calls_answer_as_unreachable_without_connecting()
    {
        using ProvisionedWatchdogClient client = Client(() => false);

        Assert.False(await client.IsReadyAsync());
        Assert.Null(await client.GetPlayerPresenceAsync());
        Assert.Null(await client.GetUpnpAsync("valheim"));

        await Task.Delay(100);
        Assert.Equal(0, _accepted);
    }

    [Fact]
    public async Task Provisioning_is_read_on_every_call()
    {
        bool provisioned = false;
        using ProvisionedWatchdogClient client = Client(() => provisioned);

        await client.IsReadyAsync();
        await Task.Delay(100);
        Assert.Equal(0, _accepted);

        provisioned = true;
        await client.IsReadyAsync();
        await Task.Delay(100);
        Assert.True(_accepted > 0, "a provisioned watchdog must be dialed");
    }

    [Fact]
    public void The_test_run_points_every_host_at_a_socket_that_cannot_exist()
    {
        Assert.Equal(NoLiveWatchdog.SocketPath, Environment.GetEnvironmentVariable("Api__WatchdogSocketPath"));
        Assert.False(File.Exists(NoLiveWatchdog.SocketPath));
    }
}
