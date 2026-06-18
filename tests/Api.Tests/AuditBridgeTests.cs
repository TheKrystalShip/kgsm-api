using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// The alert↔audit bridge gate (<see cref="KgsmAuditConsumer.IsRecoveryAction"/>): which start/restart
/// audit rows are eligible to become a resolved crash's <c>resolution.actionId</c>. A recovery is a human
/// start (operator/api/discord) or the watchdog's autonomous crash-RESTART; the watchdog's BOOT-AUTOSTART
/// (the sole system-origin <c>server.start</c>) is a fresh bring-up, not a crash recovery, so it is audited
/// but must NOT bridge — otherwise it could stash a stale id that a later crash's resolve mislinks.
/// </summary>
public sealed class AuditBridgeTests
{
    // A restart is always a recovery (the autonomous crash-heal) — origin is irrelevant.
    [Theory]
    [InlineData(AuditOrigin.System)]    // the watchdog's autonomous crash-restart (instance_restarted)
    [InlineData(AuditOrigin.Discord)]
    [InlineData(null)]
    public void Restart_IsAlwaysRecovery(string? origin)
    {
        var data = new InstanceRestartedData { InstanceName = "mc", Origin = origin };
        Assert.True(KgsmAuditConsumer.IsRecoveryAction(data, AuditAction.ServerRestart));
    }

    // A human-driven start (operator/api/discord, or the bare-CLI null origin) IS a recovery: a person
    // bringing a crashed server back is exactly the action a resolved crash should link to.
    [Theory]
    [InlineData(AuditOrigin.Discord)]
    [InlineData(AuditOrigin.Api)]
    [InlineData(AuditOrigin.Ui)]
    [InlineData(AuditOrigin.Assistant)]
    [InlineData(null)]                  // kgsm OS-user fallback — a human on the host
    public void Start_NonSystemOrigin_IsRecovery(string? origin)
    {
        var data = new InstanceStartedData { InstanceName = "mc", Origin = origin };
        Assert.True(KgsmAuditConsumer.IsRecoveryAction(data, AuditAction.ServerStart));
    }

    // A system-origin start is the watchdog boot-autostart (a caller may never declare origin=system) —
    // a fresh boot bring-up, NOT a crash recovery, so it is excluded from the bridge.
    [Theory]
    [InlineData("system")]
    [InlineData("SYSTEM")]              // matched case-insensitively
    public void Start_SystemOrigin_IsNotRecovery(string origin)
    {
        var data = new InstanceStartedData { InstanceName = "mc", Origin = origin };
        Assert.False(KgsmAuditConsumer.IsRecoveryAction(data, AuditAction.ServerStart));
    }
}
