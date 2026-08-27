using System.Text.Json;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// The library-subject audit rows — the two the engine emits, and the two this API writes because kgsm
/// emits nothing for them.
/// </summary>
/// <remarks>
/// The split is the thing worth pinning: an add and a remove are engine echoes, so a second write here
/// would be a row nothing could deduplicate; a rename and a failure have no echo to ride, so this API is
/// the only thing that can record them. These tests hold both halves and the shared rule that a library
/// row is never scoped to a server.
/// </remarks>
public sealed class LibraryAuditTests
{
    private const string HostId = "h1";
    private static readonly DateTimeOffset Ts = new(2026, 8, 22, 9, 15, 0, TimeSpan.Zero);

    private static JsonElement Data(object o) => JsonSerializer.SerializeToElement(o);

    [Fact]
    public void AddedEvent_TargetsTheLibraryAndNoServer()
    {
        var d = new LibraryAddedData
        {
            LibraryName = "ssd",
            Path = "/mnt/ssd/kgsm",
            Timestamp = Ts,
            Actor = "discord:haru",
            Origin = "ui",
        };

        AuditWrite w = AuditMapping.FromLibraryAddedEvent(d, HostId);

        Assert.Equal(AuditAction.LibraryAdd, w.Action);
        Assert.Equal(AuditTargetKind.Library, w.Target?.Kind);
        Assert.Equal("ssd", w.Target?.Id);
        // A root holds servers without being one — a serverId here would make GET /audit?serverId= return
        // a disk registration that never touched that instance.
        Assert.Null(w.ServerId);
        Assert.Equal("/mnt/ssd/kgsm", w.Meta?["path"]);
        // Provenance off the envelope, exactly as every other echo carries it.
        Assert.Equal("haru", w.Actor.Name);
        Assert.Equal("ui", w.Origin);
    }

    [Fact]
    public void RemovedEvent_SaysTheFilesSurvive()
    {
        var d = new LibraryRemovedData { LibraryName = "ssd", Path = "/mnt/ssd/kgsm", Timestamp = Ts };

        AuditWrite w = AuditMapping.FromLibraryRemovedEvent(d, HostId);

        Assert.Equal(AuditAction.LibraryRemove, w.Action);
        // Deregistering leaves instances on the disk resolving to no library — recoverable, and confusing
        // enough that the trail is where somebody works out when it started.
        Assert.Equal(AuditSeverity.Warn, w.Severity);
        Assert.Contains("untouched", w.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void RenamedEvent_NamesBothEnds()
    {
        var d = new LibraryOutcomeEventData
        {
            LibraryName = "ssd", Verb = "rename", NewName = "fast", Timestamp = Ts, Actor = "api:claude",
        };

        AuditWrite w = AuditMapping.FromLibraryRenamedEvent(d, HostId);

        Assert.Equal(AuditAction.LibraryRename, w.Action);
        Assert.Contains("ssd", w.Summary, StringComparison.Ordinal);
        Assert.Contains("fast", w.Summary, StringComparison.Ordinal);
        Assert.Equal("fast", w.Meta?["newName"]);
    }

    [Fact]
    public void FailedEvent_CarriesTheEnginesOwnWordsInMetaNotInTheSummary()
    {
        const string refusal = "library 'ssd' still holds 3 instances: factorio-1, terraria-2, necesse-3";
        var d = new LibraryOutcomeEventData
        {
            LibraryName = "ssd", Verb = "remove", Error = refusal, ExitCode = 44, Timestamp = Ts,
        };

        AuditWrite w = AuditMapping.FromLibraryFailedEvent(d, HostId);

        Assert.Equal(AuditAction.LibraryFailed, w.Action);
        // The protection working, not a fault — a removal refused because instances live there is the
        // ordinary case and must not read as a danger.
        Assert.Equal(AuditSeverity.Warn, w.Severity);
        // The sentence says what did not happen; the engine's prose is the part a reader digs into, so a
        // reworded kgsm message changes meta and never the feed.
        Assert.Equal("could not deregister library ssd", w.Summary);
        Assert.Equal(refusal, w.Meta?["error"]);
        Assert.Equal("44", w.Meta?["exitCode"]);
        Assert.Equal("remove", w.Meta?["verb"]);
    }

    [Fact]
    public void Shaping_ReadTime_MatchesTheWriteTimeMapper()
    {
        var item = new EventHistoryEntry(
            Id: "evt_kgsm_0001_0042",
            Ts: Ts,
            Type: "library.added",
            Instance: null,
            Blueprint: null,
            Actor: "discord:haru",
            Origin: "ui",
            Hostname: null,
            Data: Data(new { LibraryName = "ssd", Path = "/mnt/ssd/kgsm" }));

        AuditRecord? shaped = EngineEventShaping.Shape(item, HostId);
        Assert.NotNull(shaped);

        AuditRecord expected = AuditMapping.ToRecordDirect(
            AuditMapping.FromLibraryAddedEvent(
                new LibraryAddedData
                {
                    LibraryName = "ssd", Path = "/mnt/ssd/kgsm", Timestamp = Ts,
                    Actor = "discord:haru", Origin = "ui",
                },
                HostId),
            item.Id);

        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(shaped));
    }

    [Fact]
    public void Shaping_OwnJournalFailure_IsARealRowNotTheGenericFallback()
    {
        var item = new EventHistoryEntry(
            Id: "evt_api_0002_0007",
            Ts: Ts,
            Type: ApiJournal.LibraryFailedEvent,
            Instance: null,
            Blueprint: null,
            Actor: "api:claude",
            Origin: "api",
            Hostname: null,
            Data: Data(new { LibraryName = "ssd", Verb = "remove", Error = "still holds 3 instances", ExitCode = 44 }));

        AuditRecord? shaped = EngineEventShaping.Shape(item, HostId);

        Assert.NotNull(shaped);
        Assert.Equal(AuditAction.LibraryFailed, shaped!.Action);
        Assert.Equal("still holds 3 instances", shaped.Meta?["error"]);
    }

    [Fact]
    public void OfflineLibrary_ReportsNoCapacity()
    {
        // The never-fabricate rule where it bites hardest: an unplugged disk measured nothing, and a 0
        // free-byte figure reads as a full disk — the opposite fact, and one somebody would act on.
        var dto = new LibraryDto("archive", "/mnt/archive", Online: false, null, null, InstanceCount: 4);

        Assert.Null(dto.FreeBytes);
        Assert.Null(dto.TotalBytes);
        // The count is answered for an offline library too, which is exactly when it matters: it is what
        // a removal is refused over.
        Assert.Equal(4, dto.InstanceCount);
    }
}
