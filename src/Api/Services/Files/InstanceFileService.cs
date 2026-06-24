using System.Security.Cryptography;
using System.Text;

namespace TheKrystalShip.Api.Services.Files;

/// <summary>The outcome of a jailed file operation — a closed set the controller maps to HTTP codes.</summary>
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
/// plus the cheap PROVISIONAL <c>Editable</c>/<c>Reason</c> hint (extension/size — the content GET is
/// authoritative for binary/too-large, plan §4.3).</summary>
public sealed record FsEntry(
    string Name, string Kind, long? SizeBytes, DateTimeOffset? Mtime, bool? Editable, string? Reason);

/// <summary>Result of a directory list. <see cref="Truncated"/> ⇒ more entries exist on disk than the
/// cap returned (an honest signal, never a silent refusal — plan §5).</summary>
public sealed record ListResult(FileOp Status, string Path, bool Truncated, IReadOnlyList<FsEntry> Entries);

/// <summary>Result of a file read. <see cref="Content"/>/<see cref="Etag"/> are non-null only on
/// <see cref="FileOp.Ok"/>.</summary>
public sealed record ReadResult(
    FileOp Status, string Path, string? Content, long SizeBytes, DateTimeOffset Mtime, string? Etag);

/// <summary>Result of a file save. <see cref="Etag"/> is the new content identity on
/// <see cref="FileOp.Ok"/>.</summary>
public sealed record WriteResult(
    FileOp Status, string Path, long SizeBytes, DateTimeOffset Mtime, string? Etag);

/// <summary>The jailed, content-level file I/O for an installed instance (Tier 3 #12 — the file browser).
/// kgsm-api-owned (NOT kgsm-lib's management-file <c>IFileService</c>): kgsm supplies the jail root
/// (<c>Instance.WorkingDir</c>) via the chokepoint, and the host-filesystem read/write is done here —
/// the same shape the monitor uses reading <c>/proc</c>. Pure-ish and unit-testable against a temp dir.</summary>
public interface IInstanceFileService
{
    /// <summary>List one directory (lazy, no recursion). <paramref name="maxEntries"/> caps the result;
    /// extra entries set <see cref="ListResult.Truncated"/>.</summary>
    ListResult ListDirectory(string workingDir, string? relativePath, int maxEntries);

    /// <summary>Read a text file. Refuses binary/too-large/non-regular with the matching
    /// <see cref="FileOp"/>; on success returns the raw UTF-8 text + an sha256 etag.</summary>
    ReadResult ReadFile(string workingDir, string? relativePath, long maxBytes);

    /// <summary>Atomically overwrite an EXISTING regular text file. Optional <paramref name="ifEtag"/>
    /// gives optimistic concurrency (<see cref="FileOp.EtagMismatch"/> on drift).</summary>
    WriteResult SaveFile(string workingDir, string? relativePath, string content, string? ifEtag, long maxBytes);
}

/// <summary>
/// The default <see cref="IInstanceFileService"/>. The security model (plan §4) lives here:
/// <list type="number">
/// <item>Every request re-derives the jail (<c>realpath(WorkingDir)</c>) — never cached, instances get
///   reinstalled.</item>
/// <item>The candidate path is canonicalized following symlinks at EVERY component (POSIX realpath),
///   then required to stay within the jail — a naive <c>..</c>-strip + prefix check is insufficient
///   (an intermediate-directory symlink escapes it; verified empirically).</item>
/// <item>Only regular files are opened (<see cref="PosixFile.Lstat"/> — FIFO/socket/device refused).</item>
/// <item>Binary/size gating happens at OPEN (we have the bytes), listing stays cheap.</item>
/// <item>Writes are atomic (temp file in the same dir → fsync → rename), preserving the file mode.</item>
/// </list>
/// </summary>
public sealed class InstanceFileService : IInstanceFileService
{
    private const int MaxSymlinkHops = 64;    // symlink-loop guard
    private const int BinaryScanBytes = 8192; // NUL-byte scan window (plan §4.3)

    public ListResult ListDirectory(string workingDir, string? relativePath, int maxEntries)
    {
        if (!TryResolve(workingDir, relativePath, out string real, out string norm))
            return new ListResult(FileOp.OutOfJail, norm, false, []);

        FileKind kind = PosixFile.Lstat(real);
        if (kind == FileKind.Missing) return new ListResult(FileOp.NotFound, norm, false, []);
        if (kind != FileKind.Directory) return new ListResult(FileOp.NotADirectory, norm, false, []);

        // readdir → lstat each (one syscall/entry; cheap) → classify → sort dirs-first/alpha → cap.
        // We lstat every entry (not just the cap) so the dirs-first ordering — and therefore which
        // entries survive truncation — is deterministic; the cap is a FRONTEND render bound, not an
        // API-cost one (plan §5), and game dirs are thousands of entries at most.
        var all = new List<FsEntry>();
        IEnumerable<string> names;
        try { names = Directory.EnumerateFileSystemEntries(real); }
        catch (IOException) { return new ListResult(FileOp.NotFound, norm, false, []); }
        catch (UnauthorizedAccessException) { return new ListResult(FileOp.NotFound, norm, false, []); }

        foreach (string entryPath in names)
        {
            FsEntry? e = DescribeEntry(entryPath, maxBytesHint: long.MaxValue);
            if (e is not null) all.Add(e);
        }

        all.Sort(static (a, b) =>
        {
            bool da = a.Kind == EntryKind.Dir, db = b.Kind == EntryKind.Dir;
            if (da != db) return da ? -1 : 1; // dirs first
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        bool truncated = all.Count > maxEntries;
        IReadOnlyList<FsEntry> page = truncated ? all.GetRange(0, maxEntries) : all;
        return new ListResult(FileOp.Ok, norm, truncated, page);
    }

    public ReadResult ReadFile(string workingDir, string? relativePath, long maxBytes)
    {
        if (!TryResolve(workingDir, relativePath, out string real, out string norm))
            return new ReadResult(FileOp.OutOfJail, norm, null, 0, default, null);

        FileKind kind = PosixFile.Lstat(real);
        if (kind == FileKind.Missing) return new ReadResult(FileOp.NotFound, norm, null, 0, default, null);
        if (kind != FileKind.Regular) return new ReadResult(FileOp.NotAFile, norm, null, 0, default, null);

        var fi = new FileInfo(real);
        long size = fi.Length;
        if (size > maxBytes) return new ReadResult(FileOp.TooLarge, norm, null, size, default, null);

        byte[] bytes;
        try { bytes = File.ReadAllBytes(real); }
        catch (IOException) { return new ReadResult(FileOp.NotFound, norm, null, 0, default, null); }

        if (LooksBinary(bytes))
            return new ReadResult(FileOp.Binary, norm, null, bytes.LongLength, default, null);

        return new ReadResult(FileOp.Ok, norm,
            Encoding.UTF8.GetString(bytes), bytes.LongLength, fi.LastWriteTimeUtc, Etag(bytes));
    }

    public WriteResult SaveFile(string workingDir, string? relativePath, string content, string? ifEtag, long maxBytes)
    {
        if (!TryResolve(workingDir, relativePath, out string real, out string norm))
            return new WriteResult(FileOp.OutOfJail, norm, 0, default, null);

        FileKind kind = PosixFile.Lstat(real);
        if (kind == FileKind.Missing) return new WriteResult(FileOp.NotFound, norm, 0, default, null); // v1 = save-existing only
        if (kind != FileKind.Regular) return new WriteResult(FileOp.NotAFile, norm, 0, default, null); // refuse to clobber non-regular

        byte[] newBytes = new UTF8Encoding(false).GetBytes(content);
        if (newBytes.LongLength > maxBytes) return new WriteResult(FileOp.TooLarge, norm, 0, default, null);

        // Read the current bytes once — serves BOTH the clobber-a-binary guard and the etag check.
        byte[] current;
        try { current = File.ReadAllBytes(real); }
        catch (IOException) { return new WriteResult(FileOp.NotFound, norm, 0, default, null); }

        if (LooksBinary(current)) return new WriteResult(FileOp.Binary, norm, 0, default, null);
        if (!string.IsNullOrEmpty(ifEtag) && !string.Equals(ifEtag, Etag(current), StringComparison.Ordinal))
            return new WriteResult(FileOp.EtagMismatch, norm, 0, default, null);

        // Atomic write: temp in the SAME dir → fsync → preserve mode → rename over the target. A crash
        // mid-write must never corrupt a precious config — never truncate-in-place.
        string dir = Path.GetDirectoryName(real)!;
        string tmp = Path.Combine(dir, "." + Path.GetFileName(real) + ".tmp-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(newBytes, 0, newBytes.Length);
                fs.Flush(flushToDisk: true); // fsync
            }
            if (!OperatingSystem.IsWindows()) // mode-preserve is a no-op concept off Unix; guards CA1416
                try { File.SetUnixFileMode(tmp, File.GetUnixFileMode(real)); } catch { /* mode best-effort */ }
            File.Move(tmp, real, overwrite: true); // rename(2) — atomic on the same filesystem
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore cleanup failure */ }
            throw;
        }

        var fi = new FileInfo(real);
        return new WriteResult(FileOp.Ok, norm, newBytes.LongLength, fi.LastWriteTimeUtc, Etag(newBytes));
    }

    // ---- the jail (the load-bearing security boundary) -------------------------------------------

    /// <summary>Resolve a caller-supplied relative path inside the instance's working dir, following
    /// symlinks at every component, and require the real target to stay within the (real) jail root.
    /// Returns the resolved absolute path + the normalized relative path; <see langword="false"/> ⇒
    /// the target escapes the jail (or the input is malformed) and must be refused.</summary>
    private static bool TryResolve(string workingDir, string? relativePath, out string realTarget, out string normRel)
    {
        realTarget = "";
        normRel = "";

        string rel = (relativePath ?? "").Trim().Replace('\\', '/').Trim('/');
        if (rel.IndexOf('\0') >= 0) return false; // NUL byte — never a legitimate path

        string realRoot = CanonicalRealPath(Path.GetFullPath(workingDir));
        string lexical = Path.GetFullPath(rel.Length == 0 ? realRoot : Path.Combine(realRoot, rel));
        string real = CanonicalRealPath(lexical);

        bool contained = string.Equals(real, realRoot, StringComparison.Ordinal)
            || real.StartsWith(realRoot + "/", StringComparison.Ordinal);
        if (!contained) { normRel = rel; return false; }

        realTarget = real;
        string display = Path.GetRelativePath(realRoot, lexical);
        normRel = display is "." or "" ? "" : display.Replace('\\', '/');
        return true;
    }

    /// <summary>Canonical real path (POSIX <c>realpath</c>): resolves <c>.</c>, <c>..</c> AND symlinks at
    /// EVERY path component — following symlink chains and re-resolving their targets — so an
    /// intermediate-directory symlink (<c>working_dir/foo</c> → <c>/etc</c>, request <c>foo/passwd</c>) is
    /// caught, which a single <c>ResolveLinkTarget</c> on the leaf misses (the leaf isn't itself a link).
    /// A non-existent tail component is accepted verbatim (so a save target's not-yet-created name works).
    /// <paramref name="absolutePath"/> must be rooted.</summary>
    private static string CanonicalRealPath(string absolutePath)
    {
        var todo = new LinkedList<string>();
        foreach (string p in absolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
            todo.AddLast(p);

        var resolved = new List<string>();
        int hops = 0;
        while (todo.First is { } node)
        {
            todo.RemoveFirst();
            string comp = node.Value;
            if (comp == ".") continue;
            if (comp == "..")
            {
                if (resolved.Count > 0) resolved.RemoveAt(resolved.Count - 1);
                continue;
            }

            string current = resolved.Count == 0 ? "/" + comp : "/" + string.Join('/', resolved) + "/" + comp;
            string? link;
            try { link = new FileInfo(current).LinkTarget; }
            catch { link = null; }

            if (link is null)
            {
                resolved.Add(comp); // not a symlink (or doesn't exist) → accept verbatim
                continue;
            }

            if (++hops > MaxSymlinkHops)
                throw new IOException("symlink chain too long (possible loop)");

            // Expand: an absolute target restarts from root; a relative target is relative to the
            // link's PARENT directory (= the current `resolved`, since `comp` was not pushed). Prepend
            // the target's components to the work queue so they resolve against that parent.
            string[] parts = link.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (Path.IsPathRooted(link)) resolved.Clear();
            for (int i = parts.Length - 1; i >= 0; i--) todo.AddFirst(parts[i]);
        }

        return "/" + string.Join('/', resolved);
    }

    // ---- helpers --------------------------------------------------------------------------------

    /// <summary>Describe one directory entry from its own (un-followed) type. Symlinks are listed but
    /// marked out-of-scope (not resolved here — that's only for read/traverse); specials are non-openable;
    /// a regular file gets the cheap provisional editability hint (size only — the GET re-checks binary).</summary>
    private static FsEntry? DescribeEntry(string entryPath, long maxBytesHint)
    {
        string name = Path.GetFileName(entryPath);
        FileKind kind = PosixFile.Lstat(entryPath);
        DateTimeOffset? mtime = TryMtime(entryPath);

        switch (kind)
        {
            case FileKind.Directory:
                return new FsEntry(name, EntryKind.Dir, null, mtime, null, null);
            case FileKind.Symlink:
                return new FsEntry(name, EntryKind.Symlink, null, mtime, false, "symlink-out-of-scope");
            case FileKind.Special:
                return new FsEntry(name, EntryKind.Special, null, mtime, false, "special");
            case FileKind.Regular:
                long size;
                try { size = new FileInfo(entryPath).Length; } catch { size = 0; }
                bool tooLarge = size > maxBytesHint;
                return new FsEntry(name, EntryKind.File, size, mtime,
                    !tooLarge, tooLarge ? "too-large" : null);
            default:
                return null; // vanished between readdir and lstat — drop it
        }
    }

    private static DateTimeOffset? TryMtime(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return null; }
    }

    /// <summary>Heuristic "is this binary, not text": a NUL byte in the first 8 KB, or any invalid UTF-8
    /// sequence anywhere (strict decode). The authoritative open-time check (plan §4.3).</summary>
    private static bool LooksBinary(byte[] bytes)
    {
        int scan = Math.Min(bytes.Length, BinaryScanBytes);
        for (int i = 0; i < scan; i++)
            if (bytes[i] == 0) return true;
        try
        {
            _ = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
            return false;
        }
        catch (DecoderFallbackException) { return true; }
    }

    /// <summary>Honest content identity for optimistic concurrency — <c>sha256:&lt;hex&gt;</c> over the
    /// raw bytes (robust to mtime quirks; trivial to compute under the edit ceiling).</summary>
    private static string Etag(byte[] bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
}
