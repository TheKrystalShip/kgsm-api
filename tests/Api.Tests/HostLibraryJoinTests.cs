using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Aggregation;
using TheKrystalShip.KGSM.Core.Models.Enums;
using KgsmLibrary = TheKrystalShip.KGSM.Core.Models.Library;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// <c>HostAggregator.JoinCapacity</c> — the two-source join behind <see cref="LibraryDto"/>. The engine
/// measures whether a root is reachable and how much room it has; the monitor measures which filesystem a
/// path sits on and what disk backs it. Neither is inferred from the other, in either direction.
/// </summary>
/// <remarks>
/// The sharp case is an <b>offline</b> library. The root mount contains every absolute path, so joining one
/// would hand back the boot disk's model as the backing device of files on a disk that is gone — an invented
/// fact rendered beside a null capacity that says nothing could be measured.
/// </remarks>
public sealed class HostLibraryJoinTests
{
    // What the monitor reports on a host whose only mounted filesystem is the root one — the state a
    // library's own disk being unplugged leaves behind.
    private static readonly IReadOnlyList<DiskCapacity> RootOnly =
    [
        new DiskCapacity("/", Used: 250.0, Total: 500.0, Fs: "ext4", Device: "Samsung SSD 990 EVO Plus 1TB"),
    ];

    private static KgsmLibrary Lib(string name, string path, bool online, long? free = null, long? total = null)
        => new()
        {
            Name = name,
            Path = path,
            State = online ? LibraryState.Online : LibraryState.Offline,
            FreeBytes = free,
            TotalBytes = total,
            InstanceCount = 1,
        };

    [Fact]
    public void An_offline_library_under_root_names_no_mount_and_no_device()
    {
        // The path is under "/" and the monitor has a row for "/", so the prefix match would succeed.
        IReadOnlyList<LibraryDto> mapped =
            HostAggregator.JoinCapacity([Lib("cold", "/mnt/usb/kgsm", online: false)], RootOnly);

        LibraryDto lib = Assert.Single(mapped);
        Assert.False(lib.Online);
        Assert.Null(lib.FreeBytes);
        Assert.Null(lib.TotalBytes);
        // Nothing measured the disk behind an unreachable root, so nothing here names one.
        Assert.Null(lib.Mount);
        Assert.Null(lib.Device);
    }

    [Fact]
    public void An_online_library_on_the_same_rows_still_gets_its_mount_and_device()
    {
        IReadOnlyList<LibraryDto> mapped = HostAggregator.JoinCapacity(
            [
                Lib("cold", "/mnt/usb/kgsm", online: false),
                Lib("boot", "/srv/kgsm", online: true, free: 100_000_000_000, total: 500_000_000_000),
            ],
            RootOnly);

        LibraryDto offline = mapped.Single(l => l.Name == "cold");
        Assert.Null(offline.Mount);
        Assert.Null(offline.Device);

        LibraryDto online = mapped.Single(l => l.Name == "boot");
        Assert.Equal("/", online.Mount);
        Assert.Equal("Samsung SSD 990 EVO Plus 1TB", online.Device);
        Assert.Equal(100_000_000_000, online.FreeBytes);
    }

    [Fact]
    public void The_deeper_mount_wins_for_an_online_library()
    {
        IReadOnlyList<DiskCapacity> disks =
        [
            new DiskCapacity("/", Used: 250.0, Total: 500.0, Fs: "ext4", Device: "boot-disk"),
            new DiskCapacity("/mnt/ssd", Used: 1.0, Total: 2.0, Fs: "xfs", Device: "data-disk"),
        ];

        LibraryDto lib = Assert.Single(
            HostAggregator.JoinCapacity([Lib("fast", "/mnt/ssd/kgsm", online: true, free: 1, total: 2)], disks));

        Assert.Equal("/mnt/ssd", lib.Mount);
        Assert.Equal("data-disk", lib.Device);
    }
}
