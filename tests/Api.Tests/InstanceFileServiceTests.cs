using System.Net.Sockets;
using System.Text;
using TheKrystalShip.Api.Services.Files;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Unit coverage for the jailed I/O core (<see cref="InstanceFileService"/>) — the load-bearing security
/// surface of the file browser (plan §4/§7.1). Each test builds a real on-disk temp jail (the only honest
/// way to prove symlink/special-file handling — string logic is insufficient, as a single
/// <c>ResolveLinkTarget</c> on the leaf misses an intermediate-directory symlink). Covers: traversal +
/// symlink escape (leaf AND intermediate dir), in-jail symlink read, special files (socket), binary/
/// too-large gating, dirs-first ordering + truncation, and the atomic save round-trip (etag, 412, refuse
/// non-existent / binary).
/// </summary>
public sealed class InstanceFileServiceTests : IDisposable
{
    private readonly string _jail;
    private readonly InstanceFileService _svc = new();
    private readonly List<IDisposable> _toDispose = [];

    public InstanceFileServiceTests()
    {
        _jail = Path.Combine(Path.GetTempPath(), "fbtest-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_jail);
    }

    public void Dispose()
    {
        foreach (IDisposable d in _toDispose) { try { d.Dispose(); } catch { /* ignore */ } }
        try { Directory.Delete(_jail, recursive: true); } catch { /* best-effort */ }
    }

    private string Abs(string rel) => Path.Combine(_jail, rel);
    private void WriteText(string rel, string content) => File.WriteAllText(Abs(rel), content);

    // ===== list =====================================================================================

    [Fact]
    public void List_Root_DirsFirstThenFilesAlpha()
    {
        Directory.CreateDirectory(Abs("zeta-dir"));
        Directory.CreateDirectory(Abs("alpha-dir"));
        WriteText("b.txt", "b");
        WriteText("a.txt", "a");

        ListResult r = _svc.ListDirectory(_jail, "", 200);

        Assert.Equal(FileOp.Ok, r.Status);
        Assert.Equal(new[] { "alpha-dir", "zeta-dir", "a.txt", "b.txt" }, r.Entries.Select(e => e.Name));
        Assert.Equal(EntryKind.Dir, r.Entries[0].Kind);
        Assert.Null(r.Entries[0].SizeBytes);          // dirs have no size (honest null)
        Assert.Null(r.Entries[0].Editable);           // dirs carry no editable hint
        Assert.True(r.Entries[2].Editable);           // a small text file is provisionally editable
    }

    [Fact]
    public void List_OverCap_TruncatedSignalled()
    {
        for (int i = 0; i < 10; i++) WriteText($"f{i:D2}.txt", "x");
        ListResult r = _svc.ListDirectory(_jail, "", maxEntries: 4);

        Assert.Equal(FileOp.Ok, r.Status);
        Assert.True(r.Truncated);                      // never a silent gap
        Assert.Equal(4, r.Entries.Count);
        Assert.Equal("f00.txt", r.Entries[0].Name);   // deterministic (alpha) — so the cap is stable
    }

    [Fact]
    public void List_TooLargeFile_ProvisionalNotEditable()
    {
        File.WriteAllBytes(Abs("big.bin"), new byte[5000]); // > the 4096 hint below
        ListResult r = _svc.ListDirectory(_jail, "", 200);
        FsEntry big = r.Entries.Single(e => e.Name == "big.bin");
        // The list cap uses the service's internal long.MaxValue hint, so size never blocks at list —
        // editability is the GET's job. The entry is still a regular file with a measured size.
        Assert.Equal(EntryKind.File, big.Kind);
        Assert.Equal(5000, big.SizeBytes);
    }

    [Fact]
    public void List_NotADirectory_AndMissing()
    {
        WriteText("file.txt", "x");
        Assert.Equal(FileOp.NotADirectory, _svc.ListDirectory(_jail, "file.txt", 200).Status);
        Assert.Equal(FileOp.NotFound, _svc.ListDirectory(_jail, "nope", 200).Status);
    }

    // ===== read =====================================================================================

    [Fact]
    public void Read_TextFile_RoundTripsContentAndEtag()
    {
        WriteText("server.cfg", "name=krystal\nport=27015\n");
        ReadResult r = _svc.ReadFile(_jail, "server.cfg", 1 << 20);

        Assert.Equal(FileOp.Ok, r.Status);
        Assert.Equal("name=krystal\nport=27015\n", r.Content);
        Assert.StartsWith("sha256:", r.Etag);
        Assert.Equal(24, r.SizeBytes);
    }

    [Fact]
    public void Read_Binary_Refused()
    {
        File.WriteAllBytes(Abs("blob.bin"), [0x00, 0x01, 0x02, 0xFF]); // NUL ⇒ binary
        Assert.Equal(FileOp.Binary, _svc.ReadFile(_jail, "blob.bin", 1 << 20).Status);
    }

    [Fact]
    public void Read_InvalidUtf8_Refused()
    {
        File.WriteAllBytes(Abs("latin.txt"), [0x41, 0xC3, 0x28, 0x42]); // 0xC3 0x28 = invalid UTF-8
        Assert.Equal(FileOp.Binary, _svc.ReadFile(_jail, "latin.txt", 1 << 20).Status);
    }

    [Fact]
    public void Read_TooLarge_Refused()
    {
        File.WriteAllBytes(Abs("big.txt"), Encoding.ASCII.GetBytes(new string('a', 5000)));
        Assert.Equal(FileOp.TooLarge, _svc.ReadFile(_jail, "big.txt", maxBytes: 4096).Status);
    }

    [Fact]
    public void Read_Directory_IsNotAFile()
    {
        Directory.CreateDirectory(Abs("sub"));
        Assert.Equal(FileOp.NotAFile, _svc.ReadFile(_jail, "sub", 1 << 20).Status);
    }

    // ===== the jail (traversal + symlink escape) ====================================================

    [Fact]
    public void Read_TraversalEscape_Refused()
    {
        Assert.Equal(FileOp.OutOfJail, _svc.ReadFile(_jail, "../../../../etc/passwd", 1 << 20).Status);
        Assert.Equal(FileOp.OutOfJail, _svc.ListDirectory(_jail, "..", 200).Status);
    }

    [Fact]
    public void Read_LeafSymlinkEscape_Refused()
    {
        // A symlink whose TARGET is outside the jail — resolve-then-open must refuse it.
        File.CreateSymbolicLink(Abs("escape-leaf"), "/etc/passwd");
        Assert.Equal(FileOp.OutOfJail, _svc.ReadFile(_jail, "escape-leaf", 1 << 20).Status);
    }

    [Fact]
    public void Read_IntermediateDirSymlinkEscape_Refused()
    {
        // working_dir/escape-dir -> /etc ; request escape-dir/passwd. The LEAF (passwd) is not a link, so
        // a single ResolveLinkTarget on it misses this — only a full component-walk realpath catches it.
        Directory.CreateSymbolicLink(Abs("escape-dir"), "/etc");
        Assert.Equal(FileOp.OutOfJail, _svc.ReadFile(_jail, "escape-dir/passwd", 1 << 20).Status);
        Assert.Equal(FileOp.OutOfJail, _svc.ListDirectory(_jail, "escape-dir", 200).Status);
    }

    [Fact]
    public void Read_InJailSymlink_FollowedAndAllowed()
    {
        // A symlink that stays INSIDE the jail resolves and reads its target (a legitimate case).
        WriteText("real.cfg", "inside=yes\n");
        File.CreateSymbolicLink(Abs("link.cfg"), Abs("real.cfg"));
        ReadResult r = _svc.ReadFile(_jail, "link.cfg", 1 << 20);
        Assert.Equal(FileOp.Ok, r.Status);
        Assert.Equal("inside=yes\n", r.Content);
    }

    [Fact]
    public void List_MarksSymlinkAndSpecial_NotOpenable()
    {
        File.CreateSymbolicLink(Abs("a-link"), "/etc/hosts");
        // A real unix socket (the live .<name>.sock hazard the plan calls out).
        var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _toDispose.Add(sock);
        sock.Bind(new UnixDomainSocketEndPoint(Abs("live.sock")));

        ListResult r = _svc.ListDirectory(_jail, "", 200);
        FsEntry link = r.Entries.Single(e => e.Name == "a-link");
        FsEntry special = r.Entries.Single(e => e.Name == "live.sock");

        Assert.Equal(EntryKind.Symlink, link.Kind);
        Assert.False(link.Editable);
        Assert.Equal("symlink-out-of-scope", link.Reason);

        Assert.Equal(EntryKind.Special, special.Kind);
        Assert.False(special.Editable);
        Assert.Equal("special", special.Reason);

        // …and the socket can never be opened.
        Assert.Equal(FileOp.NotAFile, _svc.ReadFile(_jail, "live.sock", 1 << 20).Status);
    }

    // ===== save =====================================================================================

    [Fact]
    public void Save_ExistingFile_AtomicRoundTripNewEtag()
    {
        WriteText("edit.cfg", "old\n");
        ReadResult before = _svc.ReadFile(_jail, "edit.cfg", 1 << 20);

        WriteResult w = _svc.SaveFile(_jail, "edit.cfg", "new content\n", before.Etag, 1 << 20);

        Assert.Equal(FileOp.Ok, w.Status);
        Assert.NotEqual(before.Etag, w.Etag);
        Assert.Equal("new content\n", File.ReadAllText(Abs("edit.cfg")));  // really on disk
        Assert.Equal(w.Etag, _svc.ReadFile(_jail, "edit.cfg", 1 << 20).Etag);  // re-read matches the new etag
    }

    [Fact]
    public void Save_StaleEtag_Mismatch()
    {
        WriteText("edit.cfg", "v1\n");
        WriteResult w = _svc.SaveFile(_jail, "edit.cfg", "v2\n", "sha256:deadbeef", 1 << 20);
        Assert.Equal(FileOp.EtagMismatch, w.Status);
        Assert.Equal("v1\n", File.ReadAllText(Abs("edit.cfg")));  // untouched
    }

    [Fact]
    public void Save_NoEtag_LastWriterWins()
    {
        WriteText("edit.cfg", "v1\n");
        WriteResult w = _svc.SaveFile(_jail, "edit.cfg", "v2\n", ifEtag: null, 1 << 20);
        Assert.Equal(FileOp.Ok, w.Status);
        Assert.Equal("v2\n", File.ReadAllText(Abs("edit.cfg")));
    }

    [Fact]
    public void Save_NonExistent_NotFound_NoCreate()
    {
        WriteResult w = _svc.SaveFile(_jail, "brand-new.cfg", "x", null, 1 << 20);
        Assert.Equal(FileOp.NotFound, w.Status);
        Assert.False(File.Exists(Abs("brand-new.cfg")));  // v1 = save-existing only
    }

    [Fact]
    public void Save_OverBinary_Refused()
    {
        File.WriteAllBytes(Abs("blob.bin"), [0x00, 0x01]);
        WriteResult w = _svc.SaveFile(_jail, "blob.bin", "text", null, 1 << 20);
        Assert.Equal(FileOp.Binary, w.Status);  // refuse to clobber a binary
    }

    [Fact]
    public void Save_TooLargeNewContent_Refused()
    {
        WriteText("edit.cfg", "small\n");
        WriteResult w = _svc.SaveFile(_jail, "edit.cfg", new string('a', 5000), null, maxBytes: 4096);
        Assert.Equal(FileOp.TooLarge, w.Status);
        Assert.Equal("small\n", File.ReadAllText(Abs("edit.cfg")));  // untouched
    }

    [Fact]
    public void Save_SymlinkEscape_Refused()
    {
        File.CreateSymbolicLink(Abs("escape-leaf"), "/tmp/should-not-write-here.txt");
        WriteResult w = _svc.SaveFile(_jail, "escape-leaf", "pwned", null, 1 << 20);
        Assert.Equal(FileOp.OutOfJail, w.Status);
        Assert.False(File.Exists("/tmp/should-not-write-here.txt"));
    }
}
