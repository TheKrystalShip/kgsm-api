using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Engine;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// <see cref="EngineInfoService"/>'s parsers — the two seams between the engine's human-facing CLI output
/// and the <see cref="EngineInfo"/> the panel renders. Both must answer null on anything unexpected:
/// a fabricated version or path on the engine's identity card is exactly the kind of invented fact the
/// ecosystem's honesty rule exists to prevent.
/// </summary>
public sealed class EngineInfoParsingTests
{
    [Fact]
    public void Version_IsLastTokenOfBannerLine()
    {
        var result = new KgsmResult(0,
            "KGSM, version 3.18.0-rc4\nCopyright (C) 2024 TheKrystalShip\nLicense GPL-3.0\n");
        Assert.Equal("3.18.0-rc4", EngineInfoService.ParseVersion(result));
    }

    [Fact]
    public void Version_SkipsLeadingBlankLines()
    {
        var result = new KgsmResult(0, "\n\n  KGSM, version 4.0.0\n");
        Assert.Equal("4.0.0", EngineInfoService.ParseVersion(result));
    }

    [Fact]
    public void Version_FailedCommandIsNull()
    {
        Assert.Null(EngineInfoService.ParseVersion(new KgsmResult(1, "KGSM, version 3.18.0")));
    }

    [Fact]
    public void Version_EmptyOutputIsNull()
    {
        Assert.Null(EngineInfoService.ParseVersion(new KgsmResult(0, "   \n\n")));
    }

    [Fact]
    public void Paths_SelectsLayoutKeysFromBothSections()
    {
        var result = new KgsmResult(0, """
            {
              "system": { "KGSM_ROOT": "/opt/kgsm", "KGSM_SYSTEM_BLUEPRINTS_DIR": "/opt/kgsm/blueprints" },
              "user": { "KGSM_CONFIG_FILE": "/home/u/.config/kgsm/config.ini", "KGSM_INSTANCES_DIR": "/home/u/.local/share/kgsm/instances" }
            }
            """);
        EnginePaths? paths = EngineInfoService.ParsePaths(result);
        Assert.NotNull(paths);
        Assert.Equal("/opt/kgsm", paths.Root);
        Assert.Equal("/home/u/.config/kgsm/config.ini", paths.ConfigFile);
        Assert.Equal("/home/u/.local/share/kgsm/instances", paths.InstancesDir);
        Assert.Equal("/opt/kgsm/blueprints", paths.BlueprintsDir);
    }

    [Fact]
    public void Paths_MissingKeysAreNullFields()
    {
        EnginePaths? paths = EngineInfoService.ParsePaths(new KgsmResult(0, """{ "system": {}, "user": {} }"""));
        Assert.NotNull(paths);
        Assert.Null(paths.Root);
        Assert.Null(paths.ConfigFile);
        Assert.Null(paths.InstancesDir);
        Assert.Null(paths.BlueprintsDir);
    }

    [Fact]
    public void Paths_NonJsonOutputIsNull()
    {
        Assert.Null(EngineInfoService.ParsePaths(new KgsmResult(0, "usage: kgsm <command>")));
    }

    [Fact]
    public void Paths_FailedCommandIsNull()
    {
        Assert.Null(EngineInfoService.ParsePaths(new KgsmResult(2, "{}")));
    }
}
