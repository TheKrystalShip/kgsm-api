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
    public void ParseActor_LocalPrefixed_IsUserViaLocal()
    {
        // A KGSM account signed in with its own password. The provider says how this host knows them,
        // and dropping it files every local sign-in as an identity from nowhere beside the Discord
        // ones that keep theirs.
        AuditActor a = AuditMapping.ParseActor("local:claude");
        Assert.Equal(ActorKind.User, a.Kind);
        Assert.Equal("claude", a.Name);
        Assert.Equal(ActorProvider.Local, a.Provider);
    }

    /// <summary>
    /// ⚠ A rule is not a person, and reading it as one is the fabrication this path exists to prevent.
    /// </summary>
    /// <remarks>
    /// The reactor stamps <c>rule:&lt;id&gt;</c>. Nobody performed the act — a rule read a condition
    /// and concluded it — so a row claiming a user did it would assert somebody was at a keyboard at
    /// three in the morning. Who WROTE the rule rides beside the actor on the event, never in its place.
    /// </remarks>
    [Fact]
    public void ParseActor_RulePrefixed_IsARuleAndNeverAUser()
    {
        AuditActor a = AuditMapping.ParseActor("rule:give_up_backup");
        Assert.Equal(ActorKind.Rule, a.Kind);
        Assert.Equal("give_up_backup", a.Name);
        Assert.Equal(ActorProvider.Rule, a.Provider);
        Assert.NotEqual(ActorKind.User, a.Kind);
    }

    /// <summary>
    /// ⚠ An unrecognized prefix keeps the name and claims no person.
    /// </summary>
    /// <remarks>
    /// The one thing known about it is that nothing here can say who it is, which is
    /// <see cref="ActorKind.System"/> rather than <see cref="ActorKind.User"/> — a prefix nobody has
    /// taught this build about is exactly the case where guessing "a human" is least defensible.
    /// </remarks>
    [Fact]
    public void ParseActor_UnknownProvider_KeepsNameAndClaimsNoPerson()
    {
        AuditActor a = AuditMapping.ParseActor("github:octocat");
        Assert.Equal("octocat", a.Name);
        Assert.Null(a.Provider);
        Assert.Equal(ActorKind.System, a.Kind);
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
    // ⚠ Its own origin rather than "system", which is the scheduler and the engine's housekeeping:
    // those run because somebody configured a time, this one because a rule read a condition. Left
    // out, it normalizes to null and every decision reads as having come from nowhere.
    [InlineData("reactor", "reactor")]
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

        AuditWrite w = AuditMapping.FromServerEvent(data, "server.started", AuditSeverity.Info,
            "started", hostId: "primary");

        Assert.Equal("server.started", w.Action);
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

        AuditWrite w = AuditMapping.FromServerEvent(data, "server.stopped", AuditSeverity.Info,
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
            "server.updated", AuditSeverity.Info,
            new AuditTarget(AuditTargetKind.Server, "mc", "mc"), "mc", "primary", "updated mc", meta);

        AuditRecord rec = AuditMapping.ToRecord(AuditMapping.ToEntity(write, "evt_abc123"));

        Assert.Equal("evt_abc123", rec.Id);
        Assert.Equal("server.updated", rec.Action);
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
            "server.stopped", AuditSeverity.Info, null, null, "primary", "stopped", Meta: null);

        Assert.Null(AuditMapping.ToEntity(write, "evt_x").Meta);
        Assert.Null(AuditMapping.ToRecord(AuditMapping.ToEntity(write, "evt_x")).Target); // null target survives
    }

    // --- M6·0: crash events (kgsm-watchdog, system-stamped) → server.crash -------------------------
    [Fact]
    public void FromCrashEvent_IsDangerWithSystemProvenance()
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

        Assert.Equal("server.crashed", w.Action);
        Assert.Equal(AuditSeverity.Danger, w.Severity);           // going down unasked, retried or not
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

        Assert.Equal("server.crash.exhausted", w.Action);         // its own event, not a louder crash
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

        Assert.Equal("network.ports.opened", w.Action);
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

        Assert.Equal("network.ports.closed", w.Action);
        Assert.Equal(AuditSeverity.Info, w.Severity);
        Assert.Null(w.Origin);                                      // unset origin → null, never fabricated
        Assert.Contains("closed firewall ports", w.Summary);
        Assert.Equal("2456/udp", w.Meta!["ports"]);
    }

    // --- the watchdog's UPnP (router) echoes → network.upnp.open/.close, DISTINCT from ports.* --------
    [Fact]
    public void FromUpnpOpenedEvent_IsNetworkUpnpOpen_SystemProvenance_StructuredPortsMeta()
    {
        var data = new InstanceUpnpOpenedData
        {
            InstanceName = "valheim",
            Actor = "system",                                       // an autonomous daemon action
            Origin = "system",
            Ports =
            [
                new PortMapping { Start = 2456, End = 2458, Protocol = "udp" },
                new PortMapping { Start = 27015, End = 27015, Protocol = "tcp" },
            ],
        };

        AuditWrite w = AuditMapping.FromUpnpOpenedEvent(data, hostId: "primary");

        // A SEPARATE action from network.ports.open — router NAT forward, not a host ufw rule.
        Assert.Equal("network.upnp.opened", w.Action);
        Assert.NotEqual("network.ports.opened", w.Action);
        Assert.Equal(AuditSeverity.Info, w.Severity);
        Assert.Equal("system", w.Origin);
        Assert.Equal(ActorKind.System, w.Actor.Kind);
        Assert.Equal("valheim", w.ServerId);
        Assert.Contains("forwarded UPnP ports", w.Summary);
        Assert.Equal("2456-2458/udp, 27015/tcp", w.Meta!["ports"]); // range preserved; single not dashed
    }

    [Fact]
    public void FromUpnpClosedEvent_IsNetworkUpnpClose_SymmetricWithOpen()
    {
        var data = new InstanceUpnpClosedData
        {
            InstanceName = "valheim",
            Actor = "system",
            Origin = "system",
            Ports = [new PortMapping { Start = 2456, End = 2456, Protocol = "udp" }],
        };

        AuditWrite w = AuditMapping.FromUpnpClosedEvent(data, hostId: "primary");

        Assert.Equal("network.upnp.closed", w.Action);
        Assert.Equal(AuditSeverity.Info, w.Severity);
        Assert.Contains("removed UPnP ports", w.Summary);
        Assert.Equal("2456/udp", w.Meta!["ports"]);
    }

    [Fact]
    public void FromUpnpReassertedEvent_IsItsOwnAction_AtWarn_CarryingOnlyTheRestoredSubset()
    {
        // The instance also forwards 2456-2458/udp; the router dropped only the tcp one while it kept
        // running, so that is all the event carries and all the row reports. A re-assert claiming the
        // whole configured set would overstate what actually changed.
        var data = new InstanceUpnpReassertedData
        {
            InstanceName = "valheim",
            Actor = "system",
            Origin = "system",
            Ports = [new PortMapping { Start = 27015, End = 27015, Protocol = "tcp" }],
        };

        AuditWrite w = AuditMapping.FromUpnpReassertedEvent(data, hostId: "primary");

        // Distinct from BOTH the open (a bring-up) and the firewall action — this is a fact about the
        // router, and a reader counting these learns how unreliable theirs is.
        Assert.Equal("network.upnp.reasserted", w.Action);
        Assert.NotEqual("network.upnp.opened", w.Action);

        // Warn, not Info: unlike the open/close pair this is an unhealthy condition being papered over.
        Assert.Equal(AuditSeverity.Warn, w.Severity);
        Assert.Equal("system", w.Origin);
        Assert.Equal(ActorKind.System, w.Actor.Kind);
        Assert.Equal("valheim", w.ServerId);
        Assert.Equal("27015/tcp", w.Meta!["ports"]);
    }

    [Fact]
    public void ReassertIsEngineSourced_SoTheMergeTakesItFromTheJournalOnly()
    {
        // Nothing in the api re-asserts a forward — the watchdog's sweep is the only producer — so the
        // action belongs in the engine-sourced set, and a local row must never be a second source.
        Assert.Contains("network.upnp.reassert", AuditQueries.EngineSourcedActions);
    }

    // --- player.join / player.left: presence echoes (watchdog-forwarded, system/system) --------------
    [Fact]
    public void FromPlayerJoinedEvent_IsInfoPlayerJoin_IdentityInMeta_SystemProvenance()
    {
        var data = new InstancePlayerJoinedData
        {
            InstanceName = "factorio-01",
            Actor = "system",
            Origin = "system",
            PlayerId = "76561198000000000",
            PlayerName = "haru",
        };

        AuditWrite w = AuditMapping.FromPlayerJoinedEvent(data, hostId: "primary");

        Assert.Equal("player.joined", w.Action);
        Assert.Equal(AuditSeverity.Info, w.Severity);
        Assert.Equal("system", w.Origin);                         // autonomous observation
        Assert.Equal(ActorKind.System, w.Actor.Kind);
        Assert.Equal(ActorProvider.System, w.Actor.Provider);
        Assert.Equal("factorio-01", w.ServerId);
        Assert.Equal(AuditTargetKind.Server, w.Target!.Kind);     // scoped to the server, not a player kind
        Assert.Equal("factorio-01", w.Target.Id);
        Assert.Equal("haru joined factorio-01", w.Summary);       // named by display name
        Assert.Equal("76561198000000000", w.Meta!["playerId"]);
        Assert.Equal("haru", w.Meta["playerName"]);
    }

    [Fact]
    public void FromPlayerLeftEvent_NameOnly_SummaryByName_NoIdMeta()
    {
        // A name-only source: id is honestly null → omitted from meta, never stored as "".
        var data = new InstancePlayerLeftData
        {
            InstanceName = "factorio-01",
            Actor = "system",
            Origin = "system",
            PlayerId = null,
            PlayerName = "haru",
        };

        AuditWrite w = AuditMapping.FromPlayerLeftEvent(data, hostId: "primary");

        Assert.Equal("player.left", w.Action);
        Assert.Equal(AuditSeverity.Info, w.Severity);
        Assert.Equal("haru left factorio-01", w.Summary);
        Assert.False(w.Meta!.ContainsKey("playerId"));            // null id omitted, never ""
        Assert.Equal("haru", w.Meta["playerName"]);
    }

    [Fact]
    public void FromPlayerJoinedEvent_IdOnly_SummaryFallsBackToId()
    {
        // An id-only source (e.g. a steam handshake before the name resolves): the summary uses the id,
        // and only playerId lands in meta — the name is honestly absent, never fabricated.
        var data = new InstancePlayerJoinedData
        {
            InstanceName = "valheim",
            Actor = "system",
            Origin = "system",
            PlayerId = "76561198000000000",
            PlayerName = null,
        };

        AuditWrite w = AuditMapping.FromPlayerJoinedEvent(data, hostId: "primary");

        Assert.Equal("76561198000000000 joined valheim", w.Summary);
        Assert.Equal("76561198000000000", w.Meta!["playerId"]);
        Assert.False(w.Meta.ContainsKey("playerName"));
    }

    [Fact]
    public void FromPlayerEvent_BothIdentitiesAbsent_GenericSummary_NullMeta()
    {
        // Defensive: the shim guarantees at-least-one-non-null, but if a {null,null} ever arrives the
        // mapper must NOT fabricate an identity — generic summary, no meta (never an empty-string id/name).
        var data = new InstancePlayerLeftData { InstanceName = "rust", Actor = "system", Origin = "system" };

        AuditWrite w = AuditMapping.FromPlayerLeftEvent(data, hostId: "primary");

        Assert.Equal("a player left rust", w.Summary);
        Assert.Null(w.Meta);
    }

    // --- roster contract: addr / sessionKey / reason land in meta ------------------------------------
    [Fact]
    public void FromPlayerJoinedEvent_WithAddrAndSessionKey_BothLandInMeta()
    {
        // romestead-shaped: no stable account id, a real ip:port address, and the addr doubling as the
        // session token (the networking layer predicts the token type).
        var data = new InstancePlayerJoinedData
        {
            InstanceName = "romestead-1",
            Actor = "system",
            Origin = "system",
            PlayerId = null,
            PlayerName = "Aelia",
            PlayerAddr = "86.191.216.57:58845",
            SessionKey = "86.191.216.57:58845",
        };

        AuditWrite w = AuditMapping.FromPlayerJoinedEvent(data, hostId: "primary");

        Assert.Equal("86.191.216.57:58845", w.Meta!["playerAddr"]);
        Assert.Equal("86.191.216.57:58845", w.Meta["sessionKey"]);
        Assert.False(w.Meta.ContainsKey("reason")); // join never carries a reason
    }

    [Fact]
    public void FromPlayerLeftEvent_WithReasonAndSessionKey_BothLandInMeta()
    {
        // Core Keeper-shaped: an opaque userid session token + a disconnect reason on leave.
        var data = new InstancePlayerLeftData
        {
            InstanceName = "corekeeper-1",
            Actor = "system",
            Origin = "system",
            PlayerId = null,
            PlayerName = "Woltah",
            PlayerAddr = null,
            SessionKey = "3801603394",
            Reason = "App_Min",
        };

        AuditWrite w = AuditMapping.FromPlayerLeftEvent(data, hostId: "primary");

        Assert.Equal("3801603394", w.Meta!["sessionKey"]);
        Assert.Equal("App_Min", w.Meta["reason"]);
        Assert.False(w.Meta.ContainsKey("playerAddr")); // honestly absent, never ""
    }

    [Fact]
    public void FromPlayerLeftEvent_NoAddrSessionKeyOrReason_MetaOmitsAllThree()
    {
        // Pre-1.29.0-shaped payload (or a source that never populates them) — omitted, never fabricated
        // empty strings; the pre-existing playerId/playerName-only meta contract is unchanged.
        var data = new InstancePlayerLeftData
        {
            InstanceName = "factorio-01",
            Actor = "system",
            Origin = "system",
            PlayerId = "76561198000000000",
            PlayerName = "haru",
        };

        AuditWrite w = AuditMapping.FromPlayerLeftEvent(data, hostId: "primary");

        Assert.False(w.Meta!.ContainsKey("playerAddr"));
        Assert.False(w.Meta.ContainsKey("sessionKey"));
        Assert.False(w.Meta.ContainsKey("reason"));
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

    [Fact]
    public void FromInputSentEvent_IsInfoConsoleInput_FullCommandInMeta_ProvenanceRoundTrip()
    {
        // The POST /console path stamps actor+origin, so the echo carries them: discord:haru → user/haru/
        // discord (the load-bearing round-trip), origin "ui" preserved. The FULL command rides in meta.
        var data = new InstanceInputSentData
        {
            InstanceName = "factorio-01",
            Actor = "discord:haru",
            Origin = "ui",
            Command = "/ban griefer123",
        };

        AuditWrite w = AuditMapping.FromInputSentEvent(data, hostId: "primary");

        Assert.Equal("console.input.sent", w.Action);
        Assert.Equal(AuditSeverity.Info, w.Severity);
        Assert.Equal("ui", w.Origin);
        Assert.Equal(ActorKind.User, w.Actor.Kind);
        Assert.Equal("haru", w.Actor.Name);
        Assert.Equal(ActorProvider.Discord, w.Actor.Provider);
        Assert.Equal("factorio-01", w.ServerId);
        Assert.Equal(AuditTargetKind.Server, w.Target!.Kind);
        Assert.Equal("factorio-01", w.Target.Id);
        Assert.Equal("ran '/ban griefer123' on factorio-01", w.Summary);
        Assert.Equal("/ban griefer123", w.Meta!["command"]);          // FULL command, unlike config.set's key-only
    }

    [Fact]
    public void FromInputSentEvent_LongCommand_SummaryTruncated_MetaKeepsFull()
    {
        // A long command: the one-line summary is truncated (…) but meta carries the verbatim full text —
        // the trail never loses what was run.
        string full = new string('x', 200);
        var data = new InstanceInputSentData { InstanceName = "valheim", Command = full };

        AuditWrite w = AuditMapping.FromInputSentEvent(data, hostId: "primary");

        Assert.Contains("…", w.Summary);
        Assert.True(w.Summary.Length < full.Length);
        Assert.Equal(full, w.Meta!["command"]);                       // full, untruncated
        Assert.Null(w.Origin);                                        // none declared → null, never fabricated
    }

    [Fact]
    public void FromInputSentEvent_BlankCommand_CommandLessSummary_NullMeta()
    {
        // Defensive — the event guarantees a non-empty command, but a blank degrades to a command-less
        // summary + null meta, never a fabricated placeholder.
        var data = new InstanceInputSentData { InstanceName = "valheim", Command = "" };

        AuditWrite w = AuditMapping.FromInputSentEvent(data, hostId: "primary");

        Assert.Equal("console.input.sent", w.Action);
        Assert.Equal("sent a console command to valheim", w.Summary);
        Assert.Null(w.Meta);
    }

    // --- FromUpdateAvailableEvent: the engine's server.update.available echo -----------------------

    [Fact]
    public void FromUpdateAvailable_WithVersions_CarryMeta()
    {
        var ts = new DateTimeOffset(2026, 8, 10, 12, 9, 21, TimeSpan.Zero);
        var data = new InstanceUpdateAvailableData
        {
            InstanceName = "factorio-01",
            CurrentVersion = "1.0.0",
            LatestVersion = "1.1.0",
            Timestamp = ts,
            Actor = "system:scheduler",
            Origin = "system",
        };

        AuditWrite w = AuditMapping.FromUpdateAvailableEvent(data, hostId: "primary");

        Assert.Equal("server.update.available", w.Action);
        Assert.Equal(AuditSeverity.Info, w.Severity);
        // Provenance off the envelope, not assumed: a sweep is the leaf's, a hand-run check is a person's.
        Assert.Equal(ts, w.Ts);
        Assert.Equal(AuditOrigin.System, w.Origin);
        Assert.Equal(ActorKind.System, w.Actor.Kind);
        Assert.Equal("scheduler", w.Actor.Name);
        Assert.Equal(ActorProvider.System, w.Actor.Provider);
        Assert.Equal("factorio-01", w.ServerId);
        Assert.Equal(AuditTargetKind.Server, w.Target!.Kind);
        Assert.Equal("factorio-01", w.Target.Id);
        Assert.Equal("update available for factorio-01", w.Summary);
        Assert.Equal("1.0.0", w.Meta!["currentVersion"]);
        Assert.Equal("1.1.0", w.Meta!["latestVersion"]);
    }

    // A check run by hand carries the person who ran it, through whatever surface ran it — the two axes
    // stay independent and neither is derived from the other.
    [Fact]
    public void FromUpdateAvailable_CarriesAHumanActorWhenTheCheckWasRunByHand()
    {
        var data = new InstanceUpdateAvailableData
        {
            InstanceName = "factorio-01",
            CurrentVersion = "1.0.0",
            LatestVersion = "1.1.0",
            Actor = "discord:haru",
            Origin = "ui",
        };

        AuditWrite w = AuditMapping.FromUpdateAvailableEvent(data, hostId: "primary");

        Assert.Equal(ActorKind.User, w.Actor.Kind);
        Assert.Equal("haru", w.Actor.Name);
        Assert.Equal(ActorProvider.Discord, w.Actor.Provider);
        Assert.Equal(AuditOrigin.Ui, w.Origin);
    }

    [Fact]
    public void FromUpdateAvailable_NullVersions_OmitsEmptyMeta()
    {
        var data = new InstanceUpdateAvailableData { InstanceName = "factorio-01" };

        AuditWrite w = AuditMapping.FromUpdateAvailableEvent(data, hostId: "primary");

        Assert.Equal("server.update.available", w.Action);
        Assert.Null(w.Meta);  // both versions null → meta omitted, never stored as ""
    }

    [Fact]
    public void FromUpdateAvailable_EmptyInstanceName_FallsBackToDisplay()
    {
        var data = new InstanceUpdateAvailableData
        {
            InstanceName = "",
            CurrentVersion = "1.0.0",
            LatestVersion = "1.1.0",
        };

        AuditWrite w = AuditMapping.FromUpdateAvailableEvent(data, hostId: "primary");

        Assert.Equal("update available for instance", w.Summary);  // Display() fallback
        Assert.Equal("", w.ServerId);  // empty string, same as FromServerEvent
    }
}
