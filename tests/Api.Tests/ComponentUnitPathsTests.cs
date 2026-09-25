using TheKrystalShip.KGSM.ComponentSurface;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Finding a unit the way systemd finds it: a package installs its unit and its drop-ins under
/// /usr/lib, a deploy script puts them under /etc, and reading one of those two directories answers
/// correctly for one kind of host and reports the other as unwired.
///
/// The resolver is `ComponentSurface`'s, shared with every component that reads its own unit, so this
/// exercises the one implementation from the side that reads every leaf's.
/// </summary>
public class ComponentUnitPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kgsm-api-units-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private string Pinned(string name)
    {
        string dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void TheStandardRootsAreSystemdsOwnOrder()
    {
        // Highest precedence first — an administrator's copy shadows a package's, not the reverse.
        Assert.Equal(
            ["/etc/systemd/system", "/run/systemd/system", "/usr/local/lib/systemd/system", "/usr/lib/systemd/system"],
            ComponentUnitPaths.StandardRoots);
    }

    [Fact]
    public void APinnedDirectoryIsSearchedAlone()
    {
        string dir = Pinned("only");
        Assert.Equal([dir], ComponentUnitPaths.Roots(dir));
        Assert.Equal(ComponentUnitPaths.StandardRoots, ComponentUnitPaths.Roots(""));
        Assert.Equal(ComponentUnitPaths.StandardRoots, ComponentUnitPaths.Roots(null));
    }

    [Fact]
    public void AFragmentIsFoundInThePinnedDirectory()
    {
        string dir = Pinned("etc");
        Write(Path.Combine(dir, "kgsm-monitor.service"), "[Service]\n");

        Assert.Equal(Path.Combine(dir, "kgsm-monitor.service"),
            ComponentUnitPaths.Fragment("kgsm-monitor.service", dir));
    }

    [Fact]
    public void AMissingFragmentIsNullRatherThanAGuess()
    {
        Assert.Null(ComponentUnitPaths.Fragment("kgsm-nothing.service", Pinned("empty")));
    }

    [Fact]
    public void ADropInIsFoundWhereverItWasInstalled()
    {
        // The packaged case: nothing in the pinned directory except the .d that the package created.
        string dir = Pinned("usr-lib");
        Write(Path.Combine(dir, "kgsm-monitor.service.d", "50-kgsm-api-override.conf"), "[Service]\n");

        Assert.True(ComponentUnitPaths.HasDropIn("kgsm-monitor.service", "50-kgsm-api-override.conf", dir));
        Assert.False(ComponentUnitPaths.HasDropIn("kgsm-monitor.service", "50-kgsm-web.conf", dir));
    }

    [Fact]
    public void DropInsComeBackInFilenameOrder()
    {
        // systemd applies them by filename, and the floor reader depends on that: a later file's
        // Environment= is the value the unit actually runs with.
        string dir = Pinned("ordered");
        foreach (string name in new[] { "90-late.conf", "10-early.conf", "50-middle.conf" })
            Write(Path.Combine(dir, "kgsm-bot.service.d", name), "[Service]\n");
        // Not a drop-in: systemd reads *.conf and nothing else.
        Write(Path.Combine(dir, "kgsm-bot.service.d", "notes.txt"), "ignored\n");

        Assert.Equal(
            ["10-early.conf", "50-middle.conf", "90-late.conf"],
            ComponentUnitPaths.DropIns("kgsm-bot.service", dir).Select(Path.GetFileName));
    }

    [Fact]
    public void AUnitWithNoDropInsHasNone()
    {
        Assert.Empty(ComponentUnitPaths.DropIns("kgsm-bot.service", Pinned("bare")));
        Assert.False(ComponentUnitPaths.HasDropIn("kgsm-bot.service", "50-kgsm-api-override.conf", Pinned("bare2")));
    }

    [Fact]
    public void AnUnreadableRootIsNotAFailure()
    {
        // A root that does not exist is the ordinary case — most hosts have no /usr/local/lib units.
        Assert.Empty(ComponentUnitPaths.DropIns("kgsm-bot.service", Path.Combine(_root, "never-created")));
        Assert.Null(ComponentUnitPaths.Fragment("kgsm-bot.service", Path.Combine(_root, "never-created")));
    }
}
