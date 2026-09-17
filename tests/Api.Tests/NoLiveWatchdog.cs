using System.Runtime.CompilerServices;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Keeps every test host in this run off the machine's real watchdog.
/// </summary>
/// <remarks>
/// <para>
/// The settings file names the standard control socket, so a test host that configures no watchdog of
/// its own would otherwise dial the live daemon from every background service it runs. Measured on
/// hotrod: a full run did exactly that from hundreds of hosts, exhausted the daemon's file descriptors,
/// and the daemon took two running game servers down with it.
/// </para>
/// <para>
/// An environment variable sits under a host's own in-memory configuration, so a fixture that sets the
/// socket — to a stub, or to empty for "not provisioned" — still gets what it asked for. Everything else
/// gets a path inside a directory that cannot exist: provisioned, and never answering. The value cannot be
/// empty, because setting a process variable to the empty string removes it.
/// </para>
/// </remarks>
internal static class NoLiveWatchdog
{
    internal const string SocketPath = "/dev/null/kgsm-api-tests-watchdog.sock";

    [ModuleInitializer]
    internal static void Isolate() =>
        Environment.SetEnvironmentVariable("Api__WatchdogSocketPath", SocketPath);
}
