using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Pure mapping coverage (no I/O) — the fidelity of the kgsm-event → audit mapping + the flat-actor
/// round-trip is the M5 correctness risk the plan calls out. The key invariant tested is the
/// <em>round-trip</em>: what the command path stamps (<c>discord:haru</c>) parses back to the structured
/// <c>{kind:user, name:haru, provider:discord}</c>, with actor and origin kept as independent axes.
/// </summary>
public sealed class AuditMappingTests
{
    // --- ParseActor: provider:name → {kind (derived from provider), name, provider} ----------------
    [Fact]
    public void ParseActor_DiscordPrefixed_IsUserViaDiscord()
    {
        AuditActor a = AuditMapping.ParseActor("discord:haru");
        Assert.Equal(ActorKind.User, a.Kind);
        Assert.Equal("haru", a.Name);
        Assert.Equal(ActorProvider.Discord, a.Provider);
    }

    [Fact]
    public void ParseActor_ApiPrefixed_IsToken()
    {
        AuditActor a = AuditMapping.ParseActor("api:ci-deploy");
        Assert.Equal(ActorKind.Token, a.Kind);
        Assert.Equal("ci-deploy", a.Name);
        Assert.Equal(ActorProvider.Api, a.Provider);
    }

    [Fact]
    public void ParseActor_LiteralSystem_IsAutonomous()
    {
        AuditActor a = AuditMapping.ParseActor("system");
        Assert.Equal(ActorKind.System, a.Kind);
        Assert.Equal("system", a.Name);
        Assert.Equal(ActorProvider.System, a.Provider);
    }

    [Fact]
    public void ParseActor_BareOsUser_IsUserViaSystem()
    {
        // kgsm's OS-user fallback (no provider prefix): a human on the local host.
        AuditActor a = AuditMapping.ParseActor("heisen");
        Assert.Equal(ActorKind.User, a.Kind);
        Assert.Equal("heisen", a.Name);
        Assert.Equal(ActorProvider.System, a.Provider);
    }

    [Fact]
    public void ParseActor_UnknownProvider_KeepsNameButNullProvider()
    {
        // Don't fabricate a provider enum value for a prefix we don't recognize.
        AuditActor a = AuditMapping.ParseActor("github:octocat");
        Assert.Equal("octocat", a.Name);
        Assert.Null(a.Provider);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseActor_Missing_DefensiveSystem(string? flat)
    {
        AuditActor a = AuditMapping.ParseActor(flat);
        Assert.Equal(ActorKind.System, a.Kind);
        Assert.Equal(ActorProvider.System, a.Provider);
    }

    // --- NormalizeOrigin: closed set or null (never fabricated) -------------------------------------
    [Theory]
    [InlineData("ui", "ui")]
    [InlineData("API", "api")]          // case-insensitive
    [InlineData("  discord ", "discord")]
    [InlineData("system", "system")]
    public void NormalizeOrigin_Known_Passes(string raw, string expected) =>
        Assert.Equal(expected, AuditMapping.NormalizeOrigin(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bogus")]
    [InlineData("cli")]
    public void NormalizeOrigin_UnknownOrNull_IsNull(string? raw) =>
        Assert.Null(AuditMapping.NormalizeOrigin(raw));

    // --- FromServerEvent: provenance off the envelope, target/scope off the instance ---------------
    [Fact]
    public void FromServerEvent_CarriesProvenanceAndTarget()
    {
        var ts = new DateTimeOffset(2026, 6, 15, 11, 5, 18, TimeSpan.Zero);
        var data = new InstanceStartedData
        {
            InstanceName = "mc",
            Actor = "discord:haru",
            Origin = "ui",
            Timestamp = ts,
        };

        AuditWrite w = AuditMapping.FromServerEvent(data, AuditAction.ServerStart, AuditSeverity.Info,
            "started", hostId: "primary");

        Assert.Equal(AuditAction.ServerStart, w.Action);
        Assert.Equal(ts, w.Ts);                       // event time preserved, not re-stamped
        Assert.Equal("ui", w.Origin);
        Assert.Equal(ActorKind.User, w.Actor.Kind);   // discord:haru → {user, haru, discord}
        Assert.Equal("haru", w.Actor.Name);
        Assert.Equal(ActorProvider.Discord, w.Actor.Provider);
        Assert.Equal("mc", w.ServerId);
        Assert.Equal("primary", w.HostId);
        Assert.Equal(AuditTargetKind.Server, w.Target!.Kind);
        Assert.Equal("mc", w.Target.Id);
        Assert.Equal("started mc", w.Summary);
    }

    [Fact]
    public void FromServerEvent_NoOriginNoTimestamp_OriginNull_TsStamped()
    {
        var before = DateTimeOffset.UtcNow;
        var data = new InstanceStoppedData { InstanceName = "rust", Actor = "system" }; // Origin/Timestamp null

        AuditWrite w = AuditMapping.FromServerEvent(data, AuditAction.ServerStop, AuditSeverity.Info,
            "stopped", hostId: "primary");

        Assert.Null(w.Origin);                         // unset → null, never fabricated
        Assert.True(w.Ts >= before);                   // fell back to receive-time
        Assert.Equal(ActorKind.System, w.Actor.Kind);
    }

    // --- Entity <-> record round-trip incl. the meta JSON blob -------------------------------------
    [Fact]
    public void ToEntity_ToRecord_RoundTripsMeta()
    {
        var meta = new Dictionary<string, string> { ["oldVersion"] = "1", ["newVersion"] = "2" };
        var write = new AuditWrite(
            DateTimeOffset.UtcNow, "ui",
            new AuditActor(ActorKind.User, "haru", ActorProvider.Discord),
            AuditAction.ServerUpdate, AuditSeverity.Info,
            new AuditTarget(AuditTargetKind.Server, "mc", "mc"), "mc", "primary", "updated mc", meta);

        AuditRecord rec = AuditMapping.ToRecord(AuditMapping.ToEntity(write, "evt_abc123"));

        Assert.Equal("evt_abc123", rec.Id);
        Assert.Equal(AuditAction.ServerUpdate, rec.Action);
        Assert.Equal("ui", rec.Origin);
        Assert.Equal("haru", rec.Actor.Name);
        Assert.Equal(AuditTargetKind.Server, rec.Target!.Kind);
        Assert.NotNull(rec.Meta);
        Assert.Equal("1", rec.Meta!["oldVersion"]);
        Assert.Equal("2", rec.Meta["newVersion"]);
    }

    [Fact]
    public void ToEntity_EmptyMeta_StoredNull()
    {
        var write = new AuditWrite(
            DateTimeOffset.UtcNow, null,
            new AuditActor(ActorKind.System, "system", ActorProvider.System),
            AuditAction.ServerStop, AuditSeverity.Info, null, null, "primary", "stopped", Meta: null);

        Assert.Null(AuditMapping.ToEntity(write, "evt_x").Meta);
        Assert.Null(AuditMapping.ToRecord(AuditMapping.ToEntity(write, "evt_x")).Target); // null target survives
    }
}
