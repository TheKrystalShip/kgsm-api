using TheKrystalShip.Api.Services.Files;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Unit coverage for <see cref="InstanceFileService"/> — a thin HTTP-status-mapping wrapper around
/// kgsm-lib's <see cref="IInstanceFiles"/> (the single jailed filesystem
/// authority). The jail mechanics themselves (traversal/symlink
/// escape, atomic write, etag) are kgsm-lib's own responsibility and covered in
/// <c>kgsm-lib.Tests/Services/InstanceFilesTests.cs</c> — this suite instead proves: every
/// <see cref="FileOpOutcome"/> maps to the right <see cref="FileOp"/>/DTO shape, kgsm-lib's
/// <see cref="FileKind"/> maps to the right <c>Editable</c>/<c>Reason</c> presentation hint, and the
/// service passes the right arguments through (the instance name, the caller-supplied caps,
/// <c>AllowCreate:false</c>, <c>Backup:false</c>, the etag).
/// </summary>
public sealed class InstanceFileServiceTests
{
    private const string Instance = "fb-1";

    // ===== list =====================================================================================

    [Fact]
    public void List_Ok_MapsEntriesAndTruncated()
    {
        var mtime = DateTimeOffset.UtcNow;
        var fake = new FakeInstanceFiles
        {
            OnList = (_, _, _) => FileOpResult<DirListing>.Ok(new DirListing
            {
                Truncated = true,
                Entries =
                [
                    new FileEntry("a-dir", FileKind.Dir, null, mtime),
                    new FileEntry("b.txt", FileKind.File, 42, mtime),
                    new FileEntry("c-link", FileKind.Symlink, null, mtime),
                    new FileEntry("d.sock", FileKind.Special, null, mtime),
                ],
            }),
        };
        var svc = new InstanceFileService(fake);

        ListResult r = svc.ListDirectory(Instance, "sub", 200);

        Assert.Equal(FileOp.Ok, r.Status);
        Assert.True(r.Truncated);
        Assert.Equal(4, r.Entries.Count);

        FsEntry dir = r.Entries[0];
        Assert.Equal(EntryKind.Dir, dir.Kind);
        Assert.Null(dir.SizeBytes);
        Assert.Null(dir.Editable); // dirs carry no editable hint

        FsEntry file = r.Entries[1];
        Assert.Equal(EntryKind.File, file.Kind);
        Assert.Equal(42, file.SizeBytes);
        Assert.True(file.Editable);
        Assert.Null(file.Reason);

        FsEntry link = r.Entries[2];
        Assert.Equal(EntryKind.Symlink, link.Kind);
        Assert.False(link.Editable);
        Assert.Equal("symlink-out-of-scope", link.Reason);

        FsEntry special = r.Entries[3];
        Assert.Equal(EntryKind.Special, special.Kind);
        Assert.False(special.Editable);
        Assert.Equal("special", special.Reason);
    }

    [Fact]
    public void List_PassesInstanceSubdirAndMaxEntriesThrough()
    {
        (string? instance, string? subdir, int maxEntries) seen = default;
        var fake = new FakeInstanceFiles
        {
            OnList = (i, s, m) =>
            {
                seen = (i, s, m);
                return FileOpResult<DirListing>.Ok(new DirListing());
            },
        };
        var svc = new InstanceFileService(fake);

        svc.ListDirectory(Instance, "a/b", 77);

        Assert.Equal(Instance, seen.instance);
        Assert.Equal("a/b", seen.subdir);
        Assert.Equal(77, seen.maxEntries);
    }

    [Theory]
    [InlineData(FileOpOutcome.NotADirectory, FileOp.NotADirectory)]
    [InlineData(FileOpOutcome.OutOfJail, FileOp.OutOfJail)]
    [InlineData(FileOpOutcome.NotFound, FileOp.NotFound)]
    [InlineData(FileOpOutcome.InstanceUnavailable, FileOp.Unavailable)]
    [InlineData(FileOpOutcome.IoError, FileOp.NotFound)] // no dedicated surface — folds into 404, never 500
    public void List_OutcomeMapsToStatus(FileOpOutcome outcome, FileOp expected)
    {
        var fake = new FakeInstanceFiles { OnList = (_, _, _) => FileOpResult<DirListing>.Fail(outcome) };
        var svc = new InstanceFileService(fake);

        ListResult r = svc.ListDirectory(Instance, null, 200);

        Assert.Equal(expected, r.Status);
        Assert.Empty(r.Entries);
    }

    // ===== read =====================================================================================

    [Fact]
    public void Read_Ok_MapsContentSizeMtimeEtag()
    {
        var mtime = DateTimeOffset.UtcNow;
        var fake = new FakeInstanceFiles
        {
            OnRead = (_, _, _) => FileOpResult<FileContent>.Ok(new FileContent
            {
                Content = "name=krystal\n",
                SizeBytes = 13,
                Mtime = mtime,
                Etag = "sha256:deadbeef",
            }),
        };
        var svc = new InstanceFileService(fake);

        ReadResult r = svc.ReadFile(Instance, "server.cfg", 1 << 20);

        Assert.Equal(FileOp.Ok, r.Status);
        Assert.Equal("name=krystal\n", r.Content);
        Assert.Equal(13, r.SizeBytes);
        Assert.Equal(mtime, r.Mtime);
        Assert.Equal("sha256:deadbeef", r.Etag);
    }

    [Fact]
    public void Read_PassesInstancePathAndMaxBytesThrough()
    {
        (string? instance, string? relPath, long maxBytes) seen = default;
        var fake = new FakeInstanceFiles
        {
            OnRead = (i, p, m) =>
            {
                seen = (i, p, m);
                return FileOpResult<FileContent>.Ok(new FileContent());
            },
        };
        var svc = new InstanceFileService(fake);

        svc.ReadFile(Instance, "server.cfg", 4096);

        Assert.Equal(Instance, seen.instance);
        Assert.Equal("server.cfg", seen.relPath);
        Assert.Equal(4096, seen.maxBytes);
    }

    [Fact]
    public void Read_NullPath_PassedAsEmptyString()
    {
        string? seen = "unset";
        var fake = new FakeInstanceFiles
        {
            OnRead = (_, p, _) => { seen = p; return FileOpResult<FileContent>.Ok(new FileContent()); },
        };
        var svc = new InstanceFileService(fake);

        svc.ReadFile(Instance, null, 4096);

        Assert.Equal("", seen);
    }

    [Theory]
    [InlineData(FileOpOutcome.Binary, FileOp.Binary)]
    [InlineData(FileOpOutcome.TooLarge, FileOp.TooLarge)]
    [InlineData(FileOpOutcome.NotAFile, FileOp.NotAFile)]
    [InlineData(FileOpOutcome.OutOfJail, FileOp.OutOfJail)]
    [InlineData(FileOpOutcome.NotFound, FileOp.NotFound)]
    [InlineData(FileOpOutcome.InstanceUnavailable, FileOp.Unavailable)]
    public void Read_OutcomeMapsToStatus(FileOpOutcome outcome, FileOp expected)
    {
        var fake = new FakeInstanceFiles { OnRead = (_, _, _) => FileOpResult<FileContent>.Fail(outcome) };
        var svc = new InstanceFileService(fake);

        ReadResult r = svc.ReadFile(Instance, "x", 1 << 20);

        Assert.Equal(expected, r.Status);
        Assert.Null(r.Content);
    }

    // ===== save =====================================================================================

    [Fact]
    public void Save_Ok_MapsSizeMtimeEtag()
    {
        var mtime = DateTimeOffset.UtcNow;
        var fake = new FakeInstanceFiles
        {
            OnWrite = (_, _, _, _) => FileOpResult<FileStat>.Ok(new FileStat
            {
                SizeBytes = 99,
                Mtime = mtime,
                Etag = "sha256:cafebabe",
            }),
        };
        var svc = new InstanceFileService(fake);

        WriteResult r = svc.SaveFile(Instance, "edit.cfg", "new content", "sha256:old", 1 << 20);

        Assert.Equal(FileOp.Ok, r.Status);
        Assert.Equal(99, r.SizeBytes);
        Assert.Equal(mtime, r.Mtime);
        Assert.Equal("sha256:cafebabe", r.Etag);
    }

    [Fact]
    public void Save_PassesInstancePathContentAndLockedOptionsThrough()
    {
        (string? instance, string? relPath, string? content, WriteOptions? opts) seen = default;
        var fake = new FakeInstanceFiles
        {
            OnWrite = (i, p, c, o) =>
            {
                seen = (i, p, c, o);
                return FileOpResult<FileStat>.Ok(new FileStat());
            },
        };
        var svc = new InstanceFileService(fake);

        svc.SaveFile(Instance, "edit.cfg", "new content\n", "sha256:expected", maxBytes: 2_097_152);

        Assert.Equal(Instance, seen.instance);
        Assert.Equal("edit.cfg", seen.relPath);
        Assert.Equal("new content\n", seen.content);
        Assert.NotNull(seen.opts);
        Assert.False(seen.opts!.AllowCreate); // v1 = save-existing only
        Assert.False(seen.opts.Backup);       // locked: no .kgsmbak in the browser
        Assert.Equal("sha256:expected", seen.opts.ExpectedEtag);
        Assert.Equal(2_097_152, seen.opts.MaxBytes);
    }

    [Fact]
    public void Save_NullEtag_PassedAsNullExpectedEtag()
    {
        WriteOptions? seen = null;
        var fake = new FakeInstanceFiles
        {
            OnWrite = (_, _, _, o) => { seen = o; return FileOpResult<FileStat>.Ok(new FileStat()); },
        };
        var svc = new InstanceFileService(fake);

        svc.SaveFile(Instance, "edit.cfg", "x", ifEtag: null, 1 << 20);

        Assert.Null(seen!.ExpectedEtag); // null = last-writer-wins
    }

    [Theory]
    [InlineData(FileOpOutcome.EtagMismatch, FileOp.EtagMismatch)]
    [InlineData(FileOpOutcome.Binary, FileOp.Binary)]
    [InlineData(FileOpOutcome.TooLarge, FileOp.TooLarge)]
    [InlineData(FileOpOutcome.NotAFile, FileOp.NotAFile)]
    [InlineData(FileOpOutcome.NotFound, FileOp.NotFound)]
    [InlineData(FileOpOutcome.OutOfJail, FileOp.OutOfJail)]
    [InlineData(FileOpOutcome.InstanceUnavailable, FileOp.Unavailable)]
    public void Save_OutcomeMapsToStatus(FileOpOutcome outcome, FileOp expected)
    {
        var fake = new FakeInstanceFiles { OnWrite = (_, _, _, _) => FileOpResult<FileStat>.Fail(outcome) };
        var svc = new InstanceFileService(fake);

        WriteResult r = svc.SaveFile(Instance, "edit.cfg", "x", null, 1 << 20);

        Assert.Equal(expected, r.Status);
        Assert.Null(r.Etag);
    }

    // ===== fake ======================================================================================

    /// <summary>Hand-written <see cref="IInstanceFiles"/> fake (no mocking library in this project —
    /// see <c>NetworkAggregatorTests.FakeFirewall</c> for the same pattern). Switch-on-input via the
    /// <c>On*</c> delegates; <c>Delete</c>/<c>Rename</c> are unused by this surface (Phase 3b, not wired)
    /// and throw if ever called.</summary>
    // ===== find / search ============================================================================

    /// <summary>
    /// A walk match is addressed by its path, not its name: the whole point of walking is that the
    /// match was not in the directory that was asked about, so a bare name would name nothing
    /// reachable.
    /// </summary>
    [Fact]
    public void Find_Ok_CarriesThePathAndBothTruncationSignals()
    {
        var mtime = DateTimeOffset.UtcNow;
        var fake = new FakeInstanceFiles
        {
            OnFind = (_, _, _, _) => FileOpResult<FindResult>.Ok(new FindResult
            {
                Truncated = true,
                ScanLimitHit = true,
                Matches =
                [
                    new FindMatch("Pal/Saved/Config/LinuxServer/PalWorldSettings.ini", FileKind.File, 1024, mtime),
                    new FindMatch("Pal/Saved/Config", FileKind.Dir, null, mtime),
                ],
            }),
        };

        FindResults r = new InstanceFileService(fake).Find(Instance, null, "*.ini", 200);

        Assert.Equal(FileOp.Ok, r.Status);
        Assert.True(r.Truncated);
        Assert.True(r.Incomplete);
        Assert.Equal("Pal/Saved/Config/LinuxServer/PalWorldSettings.ini", r.Matches[0].Name);
        Assert.Equal(EntryKind.File, r.Matches[0].Kind);
        Assert.Equal(1024, r.Matches[0].SizeBytes);
        Assert.Equal(EntryKind.Dir, r.Matches[1].Kind);
    }

    /// <summary>
    /// "More matched than I showed" and "I stopped looking" are separate all the way out. Collapsed,
    /// the second reads as the first, and a caller concludes a file does not exist when the walk
    /// simply never reached it.
    /// </summary>
    [Fact]
    public void Find_CompleteWalk_ReportsNeitherSignal()
    {
        var fake = new FakeInstanceFiles
        {
            OnFind = (_, _, _, _) => FileOpResult<FindResult>.Ok(new FindResult { Matches = [] }),
        };

        FindResults r = new InstanceFileService(fake).Find(Instance, null, "*.ini", 200);

        Assert.Equal(FileOp.Ok, r.Status);
        Assert.False(r.Truncated);
        Assert.False(r.Incomplete);
        Assert.Empty(r.Matches);
    }

    /// <summary>
    /// An unusable pattern is the caller's and is fixed by asking differently; a path that escapes the
    /// jail is refused as not-found and never distinguished. Folding the first into the second would
    /// tell somebody their glob is a missing directory.
    /// </summary>
    [Theory]
    [InlineData(FileOpOutcome.InvalidArgument, FileOp.NotAFile)]
    [InlineData(FileOpOutcome.NotADirectory, FileOp.NotADirectory)]
    [InlineData(FileOpOutcome.OutOfJail, FileOp.OutOfJail)]
    [InlineData(FileOpOutcome.InstanceUnavailable, FileOp.Unavailable)]
    [InlineData(FileOpOutcome.NotFound, FileOp.NotFound)]
    public void Find_Failures_MapToTheirOwnOutcome(FileOpOutcome outcome, FileOp expected)
    {
        var fake = new FakeInstanceFiles
        {
            OnFind = (_, _, _, _) => FileOpResult<FindResult>.Fail(outcome, "no"),
        };

        Assert.Equal(expected, new InstanceFileService(fake).Find(Instance, null, "*", 200).Status);
    }

    [Fact]
    public void Search_Ok_CarriesTheLineItsFileAndItsNumber()
    {
        var fake = new FakeInstanceFiles
        {
            OnSearch = (_, _, _, _) => FileOpResult<FileSearchResult>.Ok(new FileSearchResult
            {
                Truncated = false,
                ScanLimitHit = true,
                Hits = [new SearchHit("server.properties", 12, "max-players=20")],
            }),
        };

        SearchResults r = new InstanceFileService(fake).Search(Instance, null, "max-players", true, 100);

        Assert.Equal(FileOp.Ok, r.Status);
        Assert.False(r.Truncated);
        Assert.True(r.Incomplete);
        SearchLine hit = Assert.Single(r.Hits);
        Assert.Equal("server.properties", hit.Path);
        Assert.Equal(12, hit.Line);
        Assert.Equal("max-players=20", hit.Text);
    }

    /// <summary>Case sensitivity is the caller's and is passed through rather than defaulted here.</summary>
    [Fact]
    public void Search_PassesTheCallersCapsAndCaseChoice()
    {
        FileSearchOptions? seen = null;
        var fake = new FakeInstanceFiles
        {
            OnSearch = (_, _, _, opts) =>
            {
                seen = opts;
                return FileOpResult<FileSearchResult>.Ok(new FileSearchResult());
            },
        };

        new InstanceFileService(fake).Search(Instance, null, "x", ignoreCase: false, maxHits: 7);

        Assert.NotNull(seen);
        Assert.False(seen!.IgnoreCase);
        Assert.Equal(7, seen.MaxHits);
    }

    private sealed class FakeInstanceFiles : IInstanceFiles
    {
        public Func<string, string?, int, FileOpResult<DirListing>>? OnList;
        public Func<string, string, long, FileOpResult<FileContent>>? OnRead;
        public Func<string, string, string, WriteOptions, FileOpResult<FileStat>>? OnWrite;

        public FileOpResult<DirListing> List(string instance, string? subdir, int maxEntries) =>
            OnList?.Invoke(instance, subdir, maxEntries) ?? FileOpResult<DirListing>.Ok(new DirListing());

        public FileOpResult<FileContent> Read(string instance, string relPath, long maxBytes) =>
            OnRead?.Invoke(instance, relPath, maxBytes) ?? FileOpResult<FileContent>.Ok(new FileContent());

        public FileOpResult<FileStat> Write(string instance, string relPath, string content, WriteOptions opts) =>
            OnWrite?.Invoke(instance, relPath, content, opts) ?? FileOpResult<FileStat>.Ok(new FileStat());

        public Func<string, string, string?, FindOptions?, FileOpResult<FindResult>>? OnFind;
        public Func<string, string, string?, FileSearchOptions?, FileOpResult<FileSearchResult>>? OnSearch;

        public FileOpResult<FindResult> Find(
            string instance, string pattern, string? subdir, FindOptions? options = null) =>
            OnFind?.Invoke(instance, pattern, subdir, options) ?? FileOpResult<FindResult>.Ok(new FindResult());

        public FileOpResult<FileSearchResult> Search(
            string instance, string pattern, string? subdir, FileSearchOptions? options = null) =>
            OnSearch?.Invoke(instance, pattern, subdir, options)
            ?? FileOpResult<FileSearchResult>.Ok(new FileSearchResult());

        public FileOpResult Delete(string instance, string relPath, DeleteOptions opts) =>
            throw new NotImplementedException("not wired by the file browser (Phase 3b)");

        public FileOpResult<FileStat> Rename(string instance, string fromRel, string toRel, RenameOptions opts) =>
            throw new NotImplementedException("not wired by the file browser (Phase 3b)");
    }
}
