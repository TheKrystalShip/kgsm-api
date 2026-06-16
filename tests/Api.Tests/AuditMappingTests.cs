using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.KGSM.Core.Models;
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

    // --- M6·0: crash events (kgsm-watchdog, system-stamped) → server.crash -------------------------
    [Fact]
    public void FromCrashEvent_IsWarnServerCrash_SystemProvenance()
    {
        var data = new InstanceCrashedData
        {
            InstanceName = "valheim",
            Actor = "system",
            Origin = "system",
            ExitCode = "139",
            Restarts = "2",
        };

        AuditWrite w = AuditMapping.FromCrashEvent(data, hostId: "primary");

        Assert.Equal(AuditAction.ServerCrash, w.Action);
        Assert.Equal(AuditSeverity.Warn, w.Severity);             // auto-restarting → warn, not danger
        Assert.Equal("system", w.Origin);                         // autonomous engine action
        Assert.Equal(ActorKind.System, w.Actor.Kind);
        Assert.Equal(ActorProvider.System, w.Actor.Provider);
        Assert.Equal("valheim", w.ServerId);
        Assert.Equal(AuditTargetKind.Server, w.Target!.Kind);
        Assert.Equal("valheim", w.Target.Id);
        Assert.Contains("auto-restarting", w.Summary);
        Assert.Equal("139", w.Meta!["exitCode"]);
        Assert.Equal("2", w.Meta["restarts"]);
    }

    [Fact]
    public void FromFailedEvent_IsDangerServerCrash_GaveUpWithCount()
    {
        var data = new InstanceFailedData
        {
            InstanceName = "rust",
            Actor = "system",
            Origin = "system",
            ExitCode = "unknown",
            Restarts = "5",
        };

        AuditWrite w = AuditMapping.FromFailedEvent(data, hostId: "primary");

        Assert.Equal(AuditAction.ServerCrash, w.Action);          // same doc-given action as a crash
        Assert.Equal(AuditSeverity.Danger, w.Severity);           // gave up → danger
        Assert.Contains("gave up", w.Summary);
        Assert.Contains("5 restart(s)", w.Summary);
        Assert.Equal("unknown", w.Meta!["exitCode"]);             // honest "unknown" preserved, not dropped
        Assert.Equal("5", w.Meta["restarts"]);
    }

    [Fact]
    public void FromFailedEvent_EmptyRestarts_OmitsCountClauseAndMeta()
    {
        var data = new InstanceFailedData { InstanceName = "ark", Actor = "system", Origin = "system" };

        AuditWrite w = AuditMapping.FromFailedEvent(data, hostId: "primary");

        Assert.DoesNotContain("restart(s)", w.Summary);           // no "after  restart(s)" with a blank count
        Assert.Null(w.Meta);                                       // both fields blank → no meta, never ""
    }

    // --- M6·0: the CLI-path firewall echo → network.ports.open -------------------------------------
    [Fact]
    public void FromPortsOpenedEvent_IsNetworkPortsOpen_WithFormattedPortsMeta()
    {
        var data = new InstancePortsOpenedData
        {
            InstanceName = "valheim",
            Actor = "discord:haru",
            Origin = "ui",
            Ports =
            [
                new PortMapping { Start = 2456, End = 2458, Protocol = "udp" },
                new PortMapping { Start = 27015, End = 27015, Protocol = "tcp" },
            ],
        };

        AuditWrite w = AuditMapping.FromPortsOpenedEvent(data, hostId: "primary");

        Assert.Equal(AuditAction.NetworkPortsOpen, w.Action);
        Assert.Equal(AuditSeverity.Info, w.Severity);
        Assert.Equal("ui", w.Origin);                             // a CLI-path open carries its real provenance
        Assert.Equal("valheim", w.ServerId);
        Assert.Equal(AuditTargetKind.Server, w.Target!.Kind);
        Assert.Equal("2456-2458/udp, 27015/tcp", w.Meta!["ports"]); // range preserved; single port not dashed
    }

    [Fact]
    public void FromPortsClosedEvent_IsNetworkPortsClose_SymmetricWithOpen()
    {
        var data = new InstancePortsClosedData
        {
            InstanceName = "valheim",
            Actor = "system",
            Origin = null,                                          // a teardown/CLI close may carry no surface
            Ports = [new PortMapping { Start = 2456, End = 2456, Protocol = "udp" }],
        };

        AuditWrite w = AuditMapping.FromPortsClosedEvent(data, hostId: "primary");

        Assert.Equal(AuditAction.NetworkPortsClose, w.Action);
        Assert.Equal(AuditSeverity.Info, w.Severity);
        Assert.Null(w.Origin);                                      // unset origin → null, never fabricated
        Assert.Contains("closed firewall ports", w.Summary);
        Assert.Equal("2456/udp", w.Meta!["ports"]);
    }

    [Theory]
    [InlineData(2456, 2456, "udp", "2456/udp")]          // single port → no dash
    [InlineData(2456, 2458, "udp", "2456-2458/udp")]     // range → dashed
    public void FormatPorts_RendersRangeAndSingle(int start, int end, string proto, string expected) =>
        Assert.Equal(expected, AuditMapping.FormatPorts(
            [new PortMapping { Start = start, End = end, Protocol = proto }]));

    [Fact]
    public void FormatPorts_Empty_IsEmptyString()
    {
        Assert.Equal("", AuditMapping.FormatPorts([]));
        Assert.Equal("", AuditMapping.FormatPorts(null));
    }

    // --- M6·b: the open_ports DIRECT write (no kgsm echo — the api owns both job + append) ---------

    [Fact]
    public void FromPortsOpenedCommand_DirectWrite_HasJobIdCorrelationAndPortsMeta()
    {
        AuditWrite w = AuditMapping.FromPortsOpenedCommand(
            serverId: "valheim",
            ports:
            [
                new PortMapping { Start = 2456, End = 2458, Protocol = "udp" },
                new PortMapping { Start = 27015, End = 27015, Protocol = "tcp" },
            ],
            actor: "discord:haru", origin: "ui", hostId: "primary", jobId: "job_abc123");

        Assert.Equal(AuditAction.NetworkPortsOpen, w.Action);
        Assert.Equal(AuditSeverity.Info, w.Severity);
        Assert.Equal("ui", w.Origin);                              // caller-declared surface, parsed like an event
        Assert.Equal(ActorKind.User, w.Actor.Kind);               // discord:haru → user via discord
        Assert.Equal("haru", w.Actor.Name);
        Assert.Equal(ActorProvider.Discord, w.Actor.Provider);
        Assert.Equal("valheim", w.ServerId);
        Assert.Equal(AuditTargetKind.Server, w.Target!.Kind);
        // The job↔audit correlation the M5 echo path could NOT provide — populatable here (direct write).
        Assert.Equal("job_abc123", w.Meta!["jobId"]);
        Assert.Equal("2456-2458/udp, 27015/tcp", w.Meta!["ports"]); // range preserved; single port not dashed
        Assert.Contains("opened firewall ports", w.Summary);
    }

    [Fact]
    public void FromPortsOpenedCommand_NoPorts_KeepsJobIdDropsPortsMetaNullOrigin()
    {
        // The runner skips an empty open, but the mapper must stay honest if called: jobId present (always),
        // ports meta omitted (nothing to record), origin null (none declared → null, never fabricated).
        AuditWrite w = AuditMapping.FromPortsOpenedCommand(
            serverId: "valheim", ports: [], actor: null, origin: null, hostId: "primary", jobId: "job_x");

        Assert.Equal("job_x", w.Meta!["jobId"]);
        Assert.False(w.Meta!.ContainsKey("ports"));
        Assert.Null(w.Origin);
    }
}
