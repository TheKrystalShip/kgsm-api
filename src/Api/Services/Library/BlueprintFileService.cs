using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Api.Services.Library;

/// <summary>The outcome of a blueprint-file operation — a closed set the controller maps to HTTP codes.
/// Mirrors kgsm-lib's <see cref="FileOpOutcome"/> (the underlying jail + validation authority), collapsed
/// to what this surface's three endpoints actually distinguish, plus <see cref="NoOriginal"/> which is a
/// kgsm-api rule rather than a kgsm-lib outcome (see <see cref="IBlueprintFileService.Revert"/>).</summary>
public enum BlueprintFileOp
{
    /// <summary>Success — the payload fields on the result are populated.</summary>
    Ok,
    /// <summary>No blueprint of that name exists in either blueprints directory (read), or no user-dir
    /// file exists to delete (revert).</summary>
    NotFound,
    /// <summary>The name is not a safe slug, or the engine's resolved path lies outside both engine-reported
    /// blueprints directories → refused (404, never reveal the host path).</summary>
    OutOfJail,
    /// <summary>The engine is unreachable or reported no blueprints directory — there is no jail root to
    /// operate against, and (on write) the engine returned no validation verdict at all.</summary>
    Unavailable,
    /// <summary>Save: the caller's <c>etag</c> no longer matches the file on disk (it changed since read).</summary>
    EtagMismatch,
    /// <summary>Save: the ENGINE rejected the content as an invalid blueprint. Nothing was written; the
    /// engine's own error strings ride on the result.</summary>
    Invalid,
    /// <summary>The file exceeds the edit-size ceiling — not opened/saved.</summary>
    TooLarge,
    /// <summary>The file's bytes are not valid UTF-8 text — not editable.</summary>
    Binary,
    /// <summary>The resolved path is not a regular file.</summary>
    NotAFile,
    /// <summary>Revert: this blueprint has no shipped original to fall back to, so deleting the user file
    /// would destroy the only copy rather than restore anything. Refused.</summary>
    NoOriginal,
    /// <summary>Create: a blueprint of that name already resolves in either directory. Refused rather than
    /// silently overwriting — editing an existing blueprint is the save path, not the create path.</summary>
    NameTaken,
    /// <summary>A filesystem failure.</summary>
    IoError,
}

/// <summary>Which blueprints directory a file lives in — the wire spelling of kgsm-lib's
/// <see cref="BlueprintTier"/>. <see cref="User"/> shadows a same-named <see cref="System"/> file
/// permanently when both exist.</summary>
public static class BlueprintTierName
{
    /// <summary>The shipped, engine-owned blueprint (never written to — it is the engine deploy's rsync
    /// target and would be erased on the next deploy).</summary>
    public const string System = "system";
    /// <summary>A blueprint in kgsm's writable user directory — either purely custom, or an override
    /// shadowing a shipped one.</summary>
    public const string User = "user";
}

/// <summary>Result of a blueprint read. Payload fields are populated only on <see cref="BlueprintFileOp.Ok"/>.</summary>
/// <param name="OverridesSystem">A user file is shadowing a shipped one of the same name — the state the
/// card's "Overridden" badge and the revert affordance both key off. Engine-sourced (the candidate list),
/// never inferred from a directory listing.</param>
public sealed record BlueprintReadResult(
    BlueprintFileOp Status, string Name, string? Content, string Tier, bool OverridesSystem,
    long SizeBytes, DateTimeOffset Mtime, string? Etag);

/// <summary>Result of a blueprint save.</summary>
/// <param name="CreatedOverride">This save is what turned a shipped-only blueprint into an overridden one
/// (there was no user file before and there is a shipped original). Distinct from
/// <paramref name="OverridesSystem"/>, which is simply the state afterwards.</param>
/// <param name="Errors">The engine validator's own messages on <see cref="BlueprintFileOp.Invalid"/>,
/// verbatim and unreworded; empty otherwise.</param>
public sealed record BlueprintSaveResult(
    BlueprintFileOp Status, long SizeBytes, DateTimeOffset Mtime, string? Etag,
    bool OverridesSystem, bool CreatedOverride, IReadOnlyList<string> Errors);

/// <summary>Result of a scaffold read. <paramref name="Content"/> is populated only on
/// <see cref="BlueprintFileOp.Ok"/>.</summary>
public sealed record BlueprintScaffoldResult(BlueprintFileOp Status, string? Content);

/// <summary>Result of a revert (deleting the user-dir override).</summary>
/// <param name="RevertedTo">The tier now serving this blueprint — always
/// <see cref="BlueprintTierName.System"/> on success, since a revert is only ever allowed when a shipped
/// original exists. The identity fields describe that now-effective file, so the caller can tell whether
/// its own re-read raced with another change.</param>
public sealed record BlueprintRevertResult(
    BlueprintFileOp Status, string RevertedTo, long SizeBytes, DateTimeOffset Mtime, string? Etag);

/// <summary>
/// Raw blueprint-file I/O for the library editor — a thin status-mapping wrapper, exactly like
/// <see cref="Files.IInstanceFileService"/>: all path resolution, jailing, engine validation, byte I/O and
/// event emission live in kgsm-lib's <see cref="IBlueprintFiles"/> (the C#↔engine chokepoint). This class
/// only translates kgsm-lib's <see cref="FileOpOutcome"/> into the <see cref="BlueprintFileOp"/> the
/// controller switches on, and derives the override/created state from the engine's candidate list
/// (<see cref="IBlueprintService.FindAll"/>) rather than from anything it works out itself.
/// </summary>
/// <remarks>
/// <para><b>Editing a shipped blueprint creates an override, always.</b> Every write lands in kgsm's user
/// blueprints directory; the shipped file is structurally unreachable from here. That is surfaced rather
/// than hidden — <see cref="BlueprintSaveResult.CreatedOverride"/> says when a save has just started
/// shadowing a shipped blueprint, and <see cref="Revert"/> undoes it.</para>
/// <para><b>No audit row is written here.</b> kgsm emits <c>blueprint_created</c>/<c>_updated</c>/
/// <c>_removed</c> for these writes, so the trail arrives as an event echo like every other engine action
/// (<c>Services/Audit/CLAUDE.md</c> — never a second writer for something kgsm already emits). The actor
/// and origin are threaded down into the emit instead, so the echoed row carries them.</para>
/// </remarks>
public interface IBlueprintFileService
{
    /// <summary>Read a blueprint's exact file text plus an sha256 etag, from EITHER blueprints directory —
    /// a shipped blueprint must be readable in order to be edited into an override.</summary>
    BlueprintReadResult Read(string name, long maxBytes);

    /// <summary>Write <paramref name="content"/> verbatim into the USER blueprints directory, after the
    /// engine has validated it. <paramref name="ifEtag"/> gives optimistic concurrency against the file
    /// that was read. <paramref name="actor"/>/<paramref name="origin"/> are stamped on the kgsm event so
    /// the audit echo attributes the edit to the real caller.</summary>
    BlueprintSaveResult Save(
        string name, string content, string? ifEtag, long maxBytes, string? actor, string? origin);

    /// <summary>The engine's blueprint skeleton, for seeding a new blueprint's buffer. Read-only; nothing
    /// is written and no event is emitted.</summary>
    BlueprintScaffoldResult Scaffold();

    /// <summary>Create a new blueprint in the USER blueprints directory. Refused with
    /// <see cref="BlueprintFileOp.NameTaken"/> when the name already resolves in either directory —
    /// changing an existing blueprint is <see cref="Save"/>, and letting create overwrite would turn a
    /// typo'd name into a silent clobber of somebody else's blueprint. The engine validates the content
    /// before anything is committed, exactly as on <see cref="Save"/>.</summary>
    BlueprintSaveResult Create(string name, string content, long maxBytes, string? actor, string? origin);

    /// <summary>Delete the user-dir override so the shipped blueprint serves again. Refused with
    /// <see cref="BlueprintFileOp.NoOriginal"/> when there is no shipped original — deleting then would
    /// destroy the only copy, which is a different operation from reverting and is not offered here.</summary>
    BlueprintRevertResult Revert(string name, string? actor, string? origin);
}

/// <summary>The default <see cref="IBlueprintFileService"/> — see the interface doc for the delegation
/// model. Depends on kgsm-lib's transient <see cref="IBlueprintFiles"/>/<see cref="IBlueprintService"/>,
/// so this is registered transient too (no captive-dependency singleton).</summary>
public sealed class BlueprintFileService(IBlueprintFiles files, IBlueprintService blueprints)
    : IBlueprintFileService
{
    public BlueprintReadResult Read(string name, long maxBytes)
    {
        if (string.IsNullOrWhiteSpace(name)) return ReadFail(BlueprintFileOp.NotFound, name);

        FileOpResult<BlueprintFileContent> r = files.ReadRaw(name, maxBytes);
        if (r.Outcome != FileOpOutcome.Ok)
            return ReadFail(MapOutcome(r.Outcome), name);

        BlueprintFileContent c = r.Value!;
        return new BlueprintReadResult(
            BlueprintFileOp.Ok, c.Name, c.Content, Tier(c.Tier), c.OverridesSystem,
            c.SizeBytes, c.Mtime, c.Etag);
    }

    public BlueprintSaveResult Save(
        string name, string content, string? ifEtag, long maxBytes, string? actor, string? origin)
    {
        if (string.IsNullOrWhiteSpace(name)) return SaveFail(BlueprintFileOp.NotFound);

        // Asked BEFORE the write so `createdOverride` reports the transition this save caused, not the
        // state it left behind (which is what the post-write answer would give). Engine-authoritative; a
        // null candidate list (engine unreachable) leaves both flags honestly false rather than guessed —
        // the write below reports the same unavailability through its own outcome.
        BlueprintCandidates? before = blueprints.FindAll(name);
        bool hadUserFile = before?.User?.Exists == true;
        bool hasOriginal = before?.HasSystemOriginal == true;

        var opts = new BlueprintWriteOptions
        {
            ExpectedEtag = ifEtag,
            MaxBytes = maxBytes,
            Actor = actor,
            Origin = origin,
        };
        FileOpResult<FileStat> r = files.WriteRaw(name, content, opts);
        if (r.Outcome != FileOpOutcome.Ok)
            return SaveFail(MapOutcome(r.Outcome), r.Errors);

        FileStat s = r.Value!;
        return new BlueprintSaveResult(
            BlueprintFileOp.Ok, s.SizeBytes, s.Mtime, s.Etag,
            OverridesSystem: hasOriginal,
            CreatedOverride: hasOriginal && !hadUserFile,
            Errors: []);
    }

    public BlueprintScaffoldResult Scaffold()
    {
        string? content = blueprints.GetScaffold();
        return content is null
            ? new BlueprintScaffoldResult(BlueprintFileOp.Unavailable, null)
            : new BlueprintScaffoldResult(BlueprintFileOp.Ok, content);
    }

    public BlueprintSaveResult Create(string name, string content, long maxBytes, string? actor, string? origin)
    {
        if (string.IsNullOrWhiteSpace(name)) return SaveFail(BlueprintFileOp.NotFound);

        // The engine's candidate list is the authority on whether the name is free — it answers for BOTH
        // directories, so a name that only exists as a shipped blueprint is taken too (creating it here
        // would silently make an override out of what the caller believes is a new game).
        if (blueprints.FindAll(name) is { } existing && (existing.User?.Exists == true || existing.HasSystemOriginal))
            return SaveFail(BlueprintFileOp.NameTaken);

        // No separate Validate pass: WriteRaw validates the content as a temp file before it ever occupies
        // the real name, and reports the engine's own errors on InvalidDraft. Validating twice would ask
        // the engine the same question and widen the window between the check and the write.
        var opts = new BlueprintWriteOptions
        {
            MaxBytes = maxBytes,
            Actor = actor,
            Origin = origin,
        };
        FileOpResult<FileStat> r = files.WriteRaw(name, content, opts);
        if (r.Outcome != FileOpOutcome.Ok)
            return SaveFail(MapOutcome(r.Outcome), r.Errors);

        FileStat s = r.Value!;
        // A created blueprint shadows nothing — the name was free in both directories a moment ago, which
        // is what the NameTaken check above established.
        return new BlueprintSaveResult(
            BlueprintFileOp.Ok, s.SizeBytes, s.Mtime, s.Etag,
            OverridesSystem: false, CreatedOverride: false, Errors: []);
    }

    public BlueprintRevertResult Revert(string name, string? actor, string? origin)
    {
        if (string.IsNullOrWhiteSpace(name)) return RevertFail(BlueprintFileOp.NotFound);

        // The no-original refusal is checked BEFORE the delete, and against the engine's candidate list
        // rather than a directory probe. kgsm-lib would happily delete a user-only blueprint — that is a
        // legitimate operation for its other callers (the assistant's authoring lane discards a probe
        // blueprint that way). It just isn't what "revert to original" means, so this surface refuses it
        // instead of destroying the only copy.
        BlueprintCandidates? before = blueprints.FindAll(name);
        if (before is null) return RevertFail(BlueprintFileOp.Unavailable);
        if (before.User?.Exists != true) return RevertFail(BlueprintFileOp.NotFound);
        if (!before.HasSystemOriginal) return RevertFail(BlueprintFileOp.NoOriginal);

        FileOpResult r = files.Remove(name, actor, origin);
        if (r.Outcome != FileOpOutcome.Ok)
            return RevertFail(MapOutcome(r.Outcome));

        // Re-read so the identity fields describe the file that is now serving. A failure here does NOT
        // fail the revert — the override is already gone, which is what was asked for; the caller reloads
        // the editor from a fresh GET regardless, and reporting an error would claim the revert didn't
        // happen when it did. Size/mtime/etag then stay honestly empty rather than carrying stale values.
        BlueprintReadResult after = Read(name, long.MaxValue);
        return after.Status == BlueprintFileOp.Ok
            ? new BlueprintRevertResult(
                BlueprintFileOp.Ok, BlueprintTierName.System, after.SizeBytes, after.Mtime, after.Etag)
            : new BlueprintRevertResult(
                BlueprintFileOp.Ok, BlueprintTierName.System, 0, default, null);
    }

    // ---- kgsm-lib outcome → the closed set this surface distinguishes ---------------------------

    private static BlueprintFileOp MapOutcome(FileOpOutcome outcome) => outcome switch
    {
        FileOpOutcome.Ok => BlueprintFileOp.Ok,
        FileOpOutcome.NotFound => BlueprintFileOp.NotFound,
        FileOpOutcome.OutOfJail => BlueprintFileOp.OutOfJail,
        FileOpOutcome.NotAFile => BlueprintFileOp.NotAFile,
        FileOpOutcome.Binary => BlueprintFileOp.Binary,
        FileOpOutcome.TooLarge => BlueprintFileOp.TooLarge,
        FileOpOutcome.EtagMismatch => BlueprintFileOp.EtagMismatch,
        FileOpOutcome.InvalidDraft => BlueprintFileOp.Invalid,
        FileOpOutcome.BlueprintsDirUnavailable => BlueprintFileOp.Unavailable,
        _ => BlueprintFileOp.IoError, // IoError / AlreadyExists (unreachable on these three paths)
    };

    private static string Tier(BlueprintTier tier) =>
        tier == BlueprintTier.User ? BlueprintTierName.User : BlueprintTierName.System;

    private static BlueprintReadResult ReadFail(BlueprintFileOp status, string name) =>
        new(status, name, null, BlueprintTierName.System, false, 0, default, null);

    private static BlueprintSaveResult SaveFail(BlueprintFileOp status, IReadOnlyList<string>? errors = null) =>
        new(status, 0, default, null, false, false, errors ?? []);

    private static BlueprintRevertResult RevertFail(BlueprintFileOp status) =>
        new(status, BlueprintTierName.System, 0, default, null);
}
