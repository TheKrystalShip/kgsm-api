using TheKrystalShip.Api.Services.Players;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// The rule behind "this server has been sitting empty": a dwell, and a latch.
/// <para>
/// Both exist to stop the notification being worthless. Without the dwell it fires every evening the last
/// person signs off for a minute; without the latch a server left running over a weekend says so every
/// tick. What is being measured is not an event — nothing happens at the moment a server becomes idle —
/// so the honesty this file guards is that the duration reported was actually observed.
/// </para>
/// </summary>
public sealed class IdleServerTests
{
    private static readonly TimeSpan Dwell = TimeSpan.FromMinutes(30);
    private static readonly DateTimeOffset T0 = new(2026, 8, 12, 20, 0, 0, TimeSpan.Zero);

    private static IdleTracker New() => new(Dwell);

    [Fact]
    public void An_empty_server_is_not_announced_before_the_dwell()
    {
        IdleTracker t = New();
        Assert.Null(t.Observe("romestead", 0, T0));
        Assert.Null(t.Observe("romestead", 0, T0.AddMinutes(29)));
    }

    [Fact]
    public void It_is_announced_once_the_dwell_passes_with_the_duration_that_was_measured()
    {
        IdleTracker t = New();
        t.Observe("romestead", 0, T0);

        TimeSpan been = Assert.NotNull(t.Observe("romestead", 0, T0.AddMinutes(31)));

        // Measured from first sight, not from a guess about when the last person left — this process may
        // have started five minutes ago, and the summary quotes this number to a human.
        Assert.Equal(TimeSpan.FromMinutes(31), been);
    }

    [Fact]
    public void It_is_announced_only_once_per_emptying()
    {
        IdleTracker t = New();
        t.Observe("romestead", 0, T0);
        Assert.NotNull(t.Observe("romestead", 0, T0.AddMinutes(31)));

        // A server left down for a fortnight is one notification, not one a minute.
        Assert.Null(t.Observe("romestead", 0, T0.AddMinutes(32)));
        Assert.Null(t.Observe("romestead", 0, T0.AddHours(9)));
    }

    [Fact]
    public void Somebody_joining_re_arms_it()
    {
        IdleTracker t = New();
        t.Observe("romestead", 0, T0);
        Assert.NotNull(t.Observe("romestead", 0, T0.AddMinutes(31)));

        t.Observe("romestead", 1, T0.AddMinutes(32));       // somebody came back
        t.Observe("romestead", 0, T0.AddMinutes(40));       // and left again — the clock restarts here

        Assert.Null(t.Observe("romestead", 0, T0.AddMinutes(60)));           // only 20 minutes in
        Assert.NotNull(t.Observe("romestead", 0, T0.AddMinutes(71)));        // now it has been 31
    }

    [Fact]
    public void A_server_that_stops_being_watchable_loses_its_clock()
    {
        IdleTracker t = New();
        t.Observe("romestead", 0, T0);

        // Retain is how the watcher says "this one was not running, or presence stopped being observable".
        // Either way the measurement cannot be continued across the gap.
        t.Retain(new HashSet<string>(StringComparer.Ordinal));

        Assert.Null(t.Observe("romestead", 0, T0.AddMinutes(31)));
        Assert.NotNull(t.Observe("romestead", 0, T0.AddMinutes(62)));
    }

    [Fact]
    public void One_servers_clock_is_not_another_servers()
    {
        IdleTracker t = New();
        t.Observe("romestead", 0, T0);
        t.Observe("factorio-01", 0, T0.AddMinutes(20));

        Assert.NotNull(t.Observe("romestead", 0, T0.AddMinutes(31)));
        Assert.Null(t.Observe("factorio-01", 0, T0.AddMinutes(31)));
    }

    [Fact]
    public void Not_knowing_is_not_the_same_as_nobody_being_connected()
    {
        IdleTracker t = New();
        t.Observe("romestead", 0, T0);

        // The supervisor stopped answering. A server genuinely idle through that gap re-arms from zero
        // rather than being announced on the strength of a hole in the record.
        t.Forget();

        Assert.Null(t.Observe("romestead", 0, T0.AddMinutes(31)));
        Assert.NotNull(t.Observe("romestead", 0, T0.AddMinutes(62)));
    }
}
