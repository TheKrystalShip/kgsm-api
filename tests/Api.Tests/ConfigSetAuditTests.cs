using System.Text.Json;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// G2 — the <c>config.set</c> audit row sourced from kgsm's <c>instance_config_changed</c> event
/// (kgsm-lib 1.22.0). The PATCH <c>/servers/{id}/config</c> path stamps actor+origin onto
/// <c>SetInstanceConfigValue</c>, so this is an echo-path mapping (no double-write).
/// <para>The load-bearing assertion is the <strong>secret-hygiene regression guard</strong>: the event
/// never carries the changed value, and the audit row must never contain it. The mapper carries only the
/// KEY into <c>meta</c> — we assert both that the key is present and that no value (and no <c>"value"</c>
/// meta entry) appears <em>anywhere</em> in the serialized row.</para>
/// </summary>
public sealed class ConfigSetAuditTests
{
    [Fact]
    public void FromConfigChanged_IsInfoConfigSet_KeyInMeta_CarriesProvenance()
    {
        var ts = new DateTimeOffset(2026, 6, 22, 9, 14, 0, TimeSpan.Zero);
        var data = new InstanceConfigChangedData
        {
            InstanceName = "factorio-01",
            Actor = "discord:haru",   // the PATCH stamps provider:name
            Origin = "ui",
            Timestamp = ts,
            Key = "server_password",  // a deliberately secret-sounding key
        };

        AuditWrite w = AuditMapping.FromConfigChangedEvent(data, hostId: "primary");

        Assert.Equal(AuditAction.ConfigSet, w.Action);
        Assert.Equal("config.set", w.Action);                 // exact wire string
        Assert.Equal(AuditSeverity.Info, w.Severity);
        Assert.Equal(ts, w.Ts);                                // event time preserved, not re-stamped
        Assert.Equal("ui", w.Origin);                          // echo carries the stamped surface
        Assert.Equal(ActorKind.User, w.Actor.Kind);           // discord:haru → {user, haru, discord}
        Assert.Equal("haru", w.Actor.Name);
        Assert.Equal(ActorProvider.Discord, w.Actor.Provider);
        Assert.Equal("factorio-01", w.ServerId);
        Assert.Equal(AuditTargetKind.Server, w.Target!.Kind);
        Assert.Equal("factorio-01", w.Target.Id);
        Assert.Equal("server_password", w.Meta!["key"]);       // KEY present
        Assert.Contains("server_password", w.Summary);
    }

    [Fact]
    public void FromConfigChanged_SecretHygiene_NoValueLeaksAnywhereInTheRow()
    {
        // The event type has NO value member, so a value cannot even be supplied. This test pins the
        // contract: drive a realistic edit, then prove the serialized row contains neither the (would-be)
        // secret value nor a "value" meta key — the regression guard for the secret-hygiene invariant.
        const string secretValue = "hunter2-super-secret";
        var data = new InstanceConfigChangedData
        {
            InstanceName = "rust",
            Actor = "discord:haru",
            Origin = "ui",
            Key = "rcon_password",
        };

        AuditWrite w = AuditMapping.FromConfigChangedEvent(data, hostId: "primary");

        // Serialize the full row exactly as it would be persisted/emitted (entity → record).
        AuditRecord rec = AuditMapping.ToRecord(AuditMapping.ToEntity(w, "evt_cfg1"));
        string json = JsonSerializer.Serialize(rec);

        Assert.DoesNotContain(secretValue, json);              // the value never appears (it was never carried)
        Assert.False(w.Meta!.ContainsKey("value"));            // no "value" meta entry, only "key"
        Assert.DoesNotContain("\"value\"", json);              // no value field anywhere in the row JSON
        Assert.Equal("rcon_password", w.Meta["key"]);          // exactly the key — that is all we record
        Assert.Single(w.Meta);                                  // meta holds ONLY the key, nothing else
    }

    [Fact]
    public void FromConfigChanged_BlankKey_KeylessSummary_NullMeta_NeverFabricated()
    {
        // Defensive: the event guarantees a non-null key, but a blank one must degrade to a key-less
        // summary with null meta — never a fabricated placeholder key.
        var data = new InstanceConfigChangedData
        {
            InstanceName = "valheim",
            Actor = "system",
            Origin = "system",
            Key = "",
        };

        AuditWrite w = AuditMapping.FromConfigChangedEvent(data, hostId: "primary");

        Assert.Equal(AuditAction.ConfigSet, w.Action);
        Assert.Contains("config changed for valheim", w.Summary);
        Assert.DoesNotContain("set config", w.Summary);
        Assert.Null(w.Meta);                                   // no key → no meta, never an empty "" entry
    }

    [Fact]
    public void FromConfigChanged_NoOriginNoTimestamp_OriginNull_TsStamped()
    {
        var before = DateTimeOffset.UtcNow;
        // A bare OS-user actor (no provider prefix), no origin/timestamp declared.
        var data = new InstanceConfigChangedData { InstanceName = "mc", Actor = "heisen", Key = "max_players" };

        AuditWrite w = AuditMapping.FromConfigChangedEvent(data, hostId: "primary");

        Assert.Null(w.Origin);                                 // unset → null, never fabricated
        Assert.True(w.Ts >= before);                           // fell back to receive-time
        Assert.Equal(ActorKind.User, w.Actor.Kind);            // OS-user fallback → user via system
        Assert.Equal("heisen", w.Actor.Name);
        Assert.Equal("max_players", w.Meta!["key"]);
    }
}
