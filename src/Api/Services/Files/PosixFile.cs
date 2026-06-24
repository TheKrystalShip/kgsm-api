using System.Runtime.InteropServices;

namespace TheKrystalShip.Api.Services.Files;

/// <summary>The Unix file-type of a path, as <c>lstat(2)</c> sees it (i.e. WITHOUT following a final
/// symlink — a symlink reports <see cref="Symlink"/>, never its target's type).</summary>
public enum FileKind
{
    /// <summary>The path does not exist (lstat <c>ENOENT</c>).</summary>
    Missing,
    /// <summary>A regular file — the only kind that may be opened/read/written.</summary>
    Regular,
    /// <summary>A directory.</summary>
    Directory,
    /// <summary>A symbolic link (NOT followed — its target is resolved separately for jail containment).</summary>
    Symlink,
    /// <summary>Anything else — FIFO, socket, char/block device. Never opened (the instance's live
    /// <c>.&lt;name&gt;.sock</c> is exactly this case; opening a FIFO would block the request thread).</summary>
    Special,
}

/// <summary>
/// A minimal <c>lstat(2)</c> wrapper — the honest file-type oracle for the file browser (plan §4.2:
/// "lstat every entry; only regular files are openable"). .NET exposes no managed API for the Unix
/// file-type bits (a socket/FIFO is indistinguishable from a regular file through <c>File.Exists</c> /
/// <c>FileInfo</c>), so we read <c>st_mode</c> directly. <see cref="FileSystemInfo.LinkTarget"/> handles
/// the symlink case in managed code; this fills the only remaining gap — telling a regular file apart
/// from a special one — which the security model depends on.
/// </summary>
/// <remarks>
/// kgsm-api is JIT (not Native-AOT), so a libc P/Invoke is allowed here (unlike kgsm-lib). The struct
/// layout used is the stable Linux <c>x86-64</c> glibc <c>struct stat</c> (144 bytes, <c>st_mode</c> at
/// offset 24) — verified empirically against a real socket/FIFO/symlink fixture on the deploy target
/// (Arch x86-64). On any other platform/architecture, or if the native call faults, the helper
/// degrades to a managed best-effort classification (a non-Linux dev box can't tell a socket from a
/// regular file — acceptable, the real host is Linux x64).
/// </remarks>
public static class PosixFile
{
    private const int StatBufSize = 144; // sizeof(struct stat), Linux x86-64 glibc
    private const int StModeOffset = 24; // offsetof(struct stat, st_mode), Linux x86-64 glibc

    // S_IFMT mask + the type values (sys/stat.h). mode_t is 32-bit.
    private const uint S_IFMT = 0xF000;
    private const uint S_IFREG = 0x8000;
    private const uint S_IFDIR = 0x4000;
    private const uint S_IFLNK = 0xA000;

    private static readonly bool _native = RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
        && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    [DllImport("libc", EntryPoint = "lstat", SetLastError = true)]
    private static extern int lstat(string path, byte[] statbuf);

    /// <summary>Classify <paramref name="path"/> by its own (un-followed) type — the <c>lstat</c> view.</summary>
    public static FileKind Lstat(string path)
    {
        if (_native)
        {
            try
            {
                byte[] buf = new byte[StatBufSize];
                if (lstat(path, buf) != 0)
                    return FileKind.Missing; // ENOENT (or EACCES on a path component) — treat as not-present
                uint type = BitConverter.ToUInt32(buf, StModeOffset) & S_IFMT;
                return type switch
                {
                    S_IFREG => FileKind.Regular,
                    S_IFDIR => FileKind.Directory,
                    S_IFLNK => FileKind.Symlink,
                    _ => FileKind.Special, // FIFO / socket / device — never openable
                };
            }
            catch (DllNotFoundException) { /* fall through to managed */ }
            catch (EntryPointNotFoundException) { /* fall through to managed */ }
        }

        // Managed best-effort (non-Linux-x64 only). Cannot distinguish a socket/FIFO from a regular
        // file, so this path is for dev boxes; the deploy target always takes the native branch.
        try
        {
            string? link = new FileInfo(path).LinkTarget;
            if (link is not null) return FileKind.Symlink;
        }
        catch { /* ignore */ }
        if (Directory.Exists(path)) return FileKind.Directory;
        if (File.Exists(path)) return FileKind.Regular;
        return FileKind.Missing;
    }
}
