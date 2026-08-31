using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Api.Services.Files;

/// <summary>The outcome of a jailed file operation — a closed set the controller maps to HTTP codes.
/// Mirrors kgsm-lib's <see cref="FileOpOutcome"/> (the underlying jail authority), collapsed to what
/// this surface's three endpoints actually distinguish.</summary>
public enum FileOp
{
    /// <summary>Success — the payload fields on the result are populated.</summary>
    Ok,
    /// <summary>The resolved real path escapes the instance's working-dir jail (a <c>..</c> climb or a
    /// symlink whose target lies outside) → the request is refused (404, never reveal the host path).</summary>
    OutOfJail,
    /// <summary>The path does not exist.</summary>
    NotFound,
    /// <summary>A list target that is not a directory.</summary>
    NotADirectory,
    /// <summary>A read/save target that is not a regular file (a dir, symlink-to-elsewhere, or special).</summary>
    NotAFile,
    /// <summary>The file's bytes are not valid UTF-8 text (a NUL byte or an invalid sequence) — not editable.</summary>
    Binary,
    /// <summary>The file exceeds the edit-size ceiling — not opened/saved.</summary>
    TooLarge,
    /// <summary>Save: the caller's <c>etag</c> no longer matches the file on disk (it changed since read).</summary>
    EtagMismatch,
    /// <summary>The instance is unknown to kgsm-lib, or its working dir could not be resolved
    /// (kgsm-lib <see cref="FileOpOutcome.InstanceUnavailable"/>) — surfaced the same way as the
    /// controller's own engine-unprovisioned pre-check: <c>503</c>.</summary>
    Unavailable,
}

/// <summary>The wire-agnostic kinds a directory entry can have (the controller maps these to the
/// <c>kind</c> string and adds presentation hints like <c>lang</c>).</summary>
public static class EntryKind
{
    public const string File = "file";
    public const string Dir = "dir";
    public const string Symlink = "symlink";
    public const string Special = "special";
}

/// <summary>One listed entry — measured facts only (size/mtime <c>null</c> when genuinely unknowable),
/// plus the kgsm-api-owned <c>Editable</c>/<c>Reason</c> presentation hint (kgsm-lib's <c>FileEntry</c>
/// carries only <c>Kind</c>/<c>SizeBytes</c>/<c>Mtime</c> — editability is a browser-surface concern).</summary>
public sealed record FsEntry(
    string Name, string Kind, long? SizeBytes, DateTimeOffset? Mtime, bool? Editable, string? Reason);

/// <summary>Result of a directory list. <see cref="Truncated"/> ⇒ more entries exist on disk than the
/// cap returned (an honest signal, never a silent refusal).</summary>
public sealed record ListResult(FileOp Status, string Path, bool Truncated, IReadOnlyList<FsEntry> Entries);

/// <summary>Result of a file read. <see cref="Content"/>/<see cref="Etag"/> are non-null only on
/// <see cref="FileOp.Ok"/>.</summary>
public sealed record ReadResult(
    FileOp Status, string Path, string? Content, long SizeBytes, DateTimeOffset Mtime, string? Etag);

/// <summary>Result of a file save. <see cref="Etag"/> is the new content identity on
/// <see cref="FileOp.Ok"/>.</summary>
public sealed record WriteResult(
    FileOp Status, string Path, long SizeBytes, DateTimeOffset Mtime, string? Etag);

/// <summary>
/// Result of a walk that matched entries by name. <see cref="Truncated"/> and
/// <see cref="Incomplete"/> stay separate all the way out: the first says more matched than were
/// returned, the second says the walk stopped before it had seen everything. Collapsed, "I stopped
/// looking" reads as "that is all there is", which is how a caller concludes a file does not exist
/// when it was simply never reached.
/// </summary>
public sealed record FindResults(
    FileOp Status, string Path, bool Truncated, bool Incomplete, IReadOnlyList<FsEntry> Matches);

/// <summary>One line a content search matched, with the file it is in and its 1-based line number.</summary>
public sealed record SearchLine(string Path, int Line, string Text);

/// <summary>Result of a content search, carrying the same two distinct truncation signals as
/// <see cref="FindResults"/> and for the same reason.</summary>
public sealed record SearchResults(
    FileOp Status, string Path, bool Truncated, bool Incomplete, IReadOnlyList<SearchLine> Hits);

/// <summary>The jailed, content-level file I/O for an installed instance (Tier 3 #12 — the file browser).
/// A thin HTTP/status-mapping wrapper: all jail resolution and byte I/O live in kgsm-lib's
/// <see cref="IInstanceFiles"/> (the single filesystem authority shared with the
/// assistant). This class only translates kgsm-lib's
/// <see cref="FileOpOutcome"/> into the <see cref="FileOp"/> the controller switches on, and adapts
/// kgsm-lib's <see cref="FileEntry"/>/<see cref="FileContent"/>/<see cref="FileStat"/> into the
/// kgsm-api-owned presentation shapes (<see cref="FsEntry"/> etc.).</summary>
public interface IInstanceFileService
{
    /// <summary>List one directory (lazy, no recursion) inside <paramref name="instance"/>'s working
    /// dir. <paramref name="maxEntries"/> caps the result; extra entries set
    /// <see cref="ListResult.Truncated"/>.</summary>
    ListResult ListDirectory(string instance, string? relativePath, int maxEntries);

    /// <summary>Read a text file. Refuses binary/too-large/non-regular with the matching
    /// <see cref="FileOp"/>; on success returns the raw UTF-8 text + an sha256 etag.</summary>
    ReadResult ReadFile(string instance, string? relativePath, long maxBytes);

    /// <summary>Atomically overwrite an EXISTING regular text file (never creates — v1 = save-existing
    /// only). Optional <paramref name="ifEtag"/> gives optimistic concurrency
    /// (<see cref="FileOp.EtagMismatch"/> on drift). Never backs up (<c>.kgsmbak</c> is off in the
    /// browser — a locked decision, unlike the assistant's writer).</summary>
    WriteResult SaveFile(string instance, string? relativePath, string content, string? ifEtag, long maxBytes);

    /// <summary>
    /// Find entries anywhere below <paramref name="relativePath"/> whose name matches
    /// <paramref name="pattern"/> (a glob), or whose path does when the pattern contains a
    /// <c>/</c>. Archived copies under a <c>backups</c> directory are excluded.
    /// </summary>
    /// <remarks>
    /// It exists because a game's own config lives at a game-specific depth — Palworld's is five levels
    /// down — and reaching it one directory listing at a time is a request per level.
    /// </remarks>
    FindResults Find(string instance, string? relativePath, string pattern, int maxResults);

    /// <summary>
    /// Search the contents of the text files below <paramref name="relativePath"/> for a regular
    /// expression, returning the matching lines. Same walk containment as <see cref="Find"/>; binaries
    /// and oversized files are skipped.
    /// </summary>
    /// <remarks>
    /// The counterpart to finding by name: a caller often knows the setting it wants and not which file
    /// holds it.
    /// </remarks>
    SearchResults Search(string instance, string? relativePath, string pattern, bool ignoreCase, int maxHits);
}

/// <summary>The default <see cref="IInstanceFileService"/> — see the interface doc for the delegation
/// model. Depends on kgsm-lib's transient <see cref="IInstanceFiles"/>, so this is registered transient
/// too (no captive-dependency singleton).</summary>
public sealed class InstanceFileService(IInstanceFiles files) : IInstanceFileService
{
    public ListResult ListDirectory(string instance, string? relativePath, int maxEntries)
    {
        string norm = NormalizeDisplayPath(relativePath);
        FileOpResult<DirListing> r = files.List(instance, relativePath, maxEntries);
        return r.Outcome switch
        {
            FileOpOutcome.Ok => new ListResult(FileOp.Ok, norm, r.Value!.Truncated,
                r.Value.Entries.Select(ToFsEntry).ToArray()),
            FileOpOutcome.NotADirectory => new ListResult(FileOp.NotADirectory, norm, false, []),
            FileOpOutcome.OutOfJail => new ListResult(FileOp.OutOfJail, norm, false, []),
            FileOpOutcome.InstanceUnavailable => new ListResult(FileOp.Unavailable, norm, false, []),
            _ => new ListResult(FileOp.NotFound, norm, false, []), // NotFound / IoError / etc.
        };
    }

    public ReadResult ReadFile(string instance, string? relativePath, long maxBytes)
    {
        string norm = NormalizeDisplayPath(relativePath);
        FileOpResult<FileContent> r = files.Read(instance, relativePath ?? "", maxBytes);
        return r.Outcome switch
        {
            FileOpOutcome.Ok => new ReadResult(FileOp.Ok, norm, r.Value!.Content, r.Value.SizeBytes, r.Value.Mtime, r.Value.Etag),
            FileOpOutcome.Binary => new ReadResult(FileOp.Binary, norm, null, 0, default, null),
            FileOpOutcome.TooLarge => new ReadResult(FileOp.TooLarge, norm, null, 0, default, null),
            FileOpOutcome.NotAFile => new ReadResult(FileOp.NotAFile, norm, null, 0, default, null),
            FileOpOutcome.OutOfJail => new ReadResult(FileOp.OutOfJail, norm, null, 0, default, null),
            FileOpOutcome.InstanceUnavailable => new ReadResult(FileOp.Unavailable, norm, null, 0, default, null),
            _ => new ReadResult(FileOp.NotFound, norm, null, 0, default, null), // NotFound / IoError / etc.
        };
    }

    public WriteResult SaveFile(string instance, string? relativePath, string content, string? ifEtag, long maxBytes)
    {
        string norm = NormalizeDisplayPath(relativePath);
        var opts = new WriteOptions
        {
            AllowCreate = false, // v1 = save-existing only (preserves the 404-on-missing behaviour)
            ExpectedEtag = ifEtag,
            Backup = false, // locked decision: no .kgsmbak clutter in the browser's file tree
            MaxBytes = maxBytes,
        };
        FileOpResult<FileStat> r = files.Write(instance, relativePath ?? "", content, opts);
        return r.Outcome switch
        {
            FileOpOutcome.Ok => new WriteResult(FileOp.Ok, norm, r.Value!.SizeBytes, r.Value.Mtime, r.Value.Etag),
            FileOpOutcome.EtagMismatch => new WriteResult(FileOp.EtagMismatch, norm, 0, default, null),
            FileOpOutcome.Binary => new WriteResult(FileOp.Binary, norm, 0, default, null),
            FileOpOutcome.TooLarge => new WriteResult(FileOp.TooLarge, norm, 0, default, null),
            FileOpOutcome.NotAFile => new WriteResult(FileOp.NotAFile, norm, 0, default, null),
            FileOpOutcome.OutOfJail => new WriteResult(FileOp.OutOfJail, norm, 0, default, null),
            FileOpOutcome.InstanceUnavailable => new WriteResult(FileOp.Unavailable, norm, 0, default, null),
            _ => new WriteResult(FileOp.NotFound, norm, 0, default, null), // NotFound (no create) / IoError / etc.
        };
    }

    public FindResults Find(string instance, string? relativePath, string pattern, int maxResults)
    {
        string norm = NormalizeDisplayPath(relativePath);
        FileOpResult<FindResult> r = files.Find(
            instance, pattern, relativePath, new FindOptions(MaxResults: maxResults));

        return r.Outcome switch
        {
            FileOpOutcome.Ok => new FindResults(
                FileOp.Ok, norm, r.Value!.Truncated, r.Value.ScanLimitHit,
                [.. r.Value.Matches.Select(ToFsEntry)]),
            // A pattern that cannot be compiled is the caller's, not the path's — it is the one thing
            // here that is fixed by asking differently, so it is not folded into "not found".
            FileOpOutcome.InvalidArgument => new FindResults(FileOp.NotAFile, norm, false, false, []),
            FileOpOutcome.NotADirectory => new FindResults(FileOp.NotADirectory, norm, false, false, []),
            FileOpOutcome.OutOfJail => new FindResults(FileOp.OutOfJail, norm, false, false, []),
            FileOpOutcome.InstanceUnavailable => new FindResults(FileOp.Unavailable, norm, false, false, []),
            _ => new FindResults(FileOp.NotFound, norm, false, false, []),
        };
    }

    public SearchResults Search(
        string instance, string? relativePath, string pattern, bool ignoreCase, int maxHits)
    {
        string norm = NormalizeDisplayPath(relativePath);
        FileOpResult<FileSearchResult> r = files.Search(
            instance, pattern, relativePath, new FileSearchOptions(MaxHits: maxHits, IgnoreCase: ignoreCase));

        return r.Outcome switch
        {
            FileOpOutcome.Ok => new SearchResults(
                FileOp.Ok, norm, r.Value!.Truncated, r.Value.ScanLimitHit,
                [.. r.Value.Hits.Select(h => new SearchLine(h.Path, h.LineNumber, h.Line))]),
            FileOpOutcome.InvalidArgument => new SearchResults(FileOp.NotAFile, norm, false, false, []),
            FileOpOutcome.NotADirectory => new SearchResults(FileOp.NotADirectory, norm, false, false, []),
            FileOpOutcome.OutOfJail => new SearchResults(FileOp.OutOfJail, norm, false, false, []),
            FileOpOutcome.InstanceUnavailable => new SearchResults(FileOp.Unavailable, norm, false, false, []),
            _ => new SearchResults(FileOp.NotFound, norm, false, false, []),
        };
    }

    // ---- kgsm-lib DTO → kgsm-api presentation DTO --------------------------------------------------

    /// <summary>A <see cref="FileKind.File"/> entry is provisionally editable (the content GET is
    /// authoritative for binary/too-large); a symlink is listed but out-of-scope (never resolved for
    /// listing); anything else (FIFO/socket/device) is never openable.</summary>
    /// <summary>A walk match, which carries a path rather than a name — the path is what makes it
    /// reachable, since the whole point of the walk is that it was not in the directory asked about.</summary>
    private static FsEntry ToFsEntry(FindMatch m) => m.Kind switch
    {
        FileKind.Dir => new FsEntry(m.Path, EntryKind.Dir, null, m.Mtime, null, null),
        FileKind.Symlink => new FsEntry(m.Path, EntryKind.Symlink, null, m.Mtime, false, "symlink-out-of-scope"),
        FileKind.Special => new FsEntry(m.Path, EntryKind.Special, null, m.Mtime, false, "special"),
        _ => new FsEntry(m.Path, EntryKind.File, m.SizeBytes, m.Mtime, true, null),
    };

    private static FsEntry ToFsEntry(FileEntry e) => e.Kind switch
    {
        FileKind.Dir => new FsEntry(e.Name, EntryKind.Dir, null, e.Mtime, null, null),
        FileKind.Symlink => new FsEntry(e.Name, EntryKind.Symlink, null, e.Mtime, false, "symlink-out-of-scope"),
        FileKind.Special => new FsEntry(e.Name, EntryKind.Special, null, e.Mtime, false, "special"),
        _ => new FsEntry(e.Name, EntryKind.File, e.SizeBytes, e.Mtime, true, null), // FileKind.File
    };

    /// <summary>The relative path echoed back on the DTOs — kgsm-lib doesn't return the resolved
    /// canonical form, so this is just the caller's own input, slash-normalized and trimmed.</summary>
    private static string NormalizeDisplayPath(string? relativePath) =>
        (relativePath ?? "").Trim().Replace('\\', '/').Trim('/');
}
