using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Services.Players;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Unit coverage for the resolution step the moderation endpoints delegate to — the point where a
/// roster record plus a blueprint template become the token the game is handed.
/// </summary>
public sealed class ModerationTargetResolverTests
{
    private static RosterPlayer Player(string? id = null, string? name = null, string? addr = null) =>
        new("identity-1", id, name, addr, PlayerStatus.online,
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow, null);

    [Fact]
    public void IpTemplate_ResolvesTheAddressWithoutItsPort()
    {
        var failure = ModerationTargetResolver.TryResolve(
            "kick {ip}", Player(addr: "95.19.50.122:61543"), out string target, out ModerationTargetKind kind);

        Assert.Equal(ModerationTargetResolver.Failure.None, failure);
        Assert.Equal("95.19.50.122", target);
        Assert.Equal(ModerationTargetKind.Ip, kind);
    }

    [Fact]
    public void NameTemplate_ResolvesTheName_EvenWhenAnAddressIsAlsoPresent()
    {
        // The blueprint decides which identity, not whichever field looks most specific.
        var failure = ModerationTargetResolver.TryResolve(
            "ban {name}", Player(name: "Notch", addr: "10.0.0.5:2222"), out string target, out _);

        Assert.Equal(ModerationTargetResolver.Failure.None, failure);
        Assert.Equal("Notch", target);
    }

    [Fact]
    public void IdTemplate_ResolvesTheAccountId()
    {
        var failure = ModerationTargetResolver.TryResolve(
            "banid {id}", Player(id: "76561198000000000", name: "Heisen"), out string target, out _);

        Assert.Equal(ModerationTargetResolver.Failure.None, failure);
        Assert.Equal("76561198000000000", target);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("kick")]
    public void NoUsableTemplate_IsUnsupported(string? template)
    {
        var failure = ModerationTargetResolver.TryResolve(
            template, Player(addr: "1.2.3.4:5"), out string target, out _);

        Assert.Equal(ModerationTargetResolver.Failure.Unsupported, failure);
        Assert.Equal(string.Empty, target);
    }

    [Fact]
    public void PlayerMissingTheRequestedIdentity_IsRefused_NotSubstituted()
    {
        // A Steam-relay game exposes no address. Falling back to the name here would ban a different
        // thing than the operator asked for.
        var failure = ModerationTargetResolver.TryResolve(
            "kick {ip}", Player(name: "NoAddressHere"), out string target, out _);

        Assert.Equal(ModerationTargetResolver.Failure.NoSuchIdentity, failure);
        Assert.Equal(string.Empty, target);
    }

    [Theory]
    [InlineData("95.19.50.122:61543", "95.19.50.122")]
    [InlineData("95.19.50.122", "95.19.50.122")]
    [InlineData("[::1]:9999", "::1")]
    [InlineData("[2001:db8::1]:443", "2001:db8::1")]
    // A bare IPv6 literal carries no port; truncating at the last colon would mangle the address.
    [InlineData("2001:db8::1", "2001:db8::1")]
    [InlineData("::1", "::1")]
    // A trailing colon-something that is not numeric is not a port, so it is left alone.
    [InlineData("host.example:notaport", "host.example:notaport")]
    public void AddressOnly_StripsOnlyARealTrailingPort(string input, string expected)
    {
        Assert.Equal(expected, ModerationTargetResolver.AddressOnly(input));
    }

    [Fact]
    public void Describe_ReportsEachActionIndependently()
    {
        // A game can support kicking without supporting bans; the capability must not round up.
        var instance = new Instance { Name = "x", KickCommand = "kick {name}", BanCommand = "", UnbanCommand = "" };

        ModerationCapability cap = ModerationTargetResolver.Describe(instance);

        Assert.True(cap.Kick);
        Assert.False(cap.Ban);
        Assert.False(cap.Unban);
        Assert.Equal("name", cap.TargetKind);
    }

    [Fact]
    public void Describe_NoModeration_ClaimsNothingAndNamesNoKind()
    {
        ModerationCapability cap = ModerationTargetResolver.Describe(new Instance { Name = "x" });

        Assert.False(cap.Kick);
        Assert.False(cap.Ban);
        Assert.False(cap.Unban);
        Assert.Null(cap.TargetKind);
    }

    [Fact]
    public void Describe_NullInstance_ClaimsNothing()
    {
        ModerationCapability cap = ModerationTargetResolver.Describe((Instance?)null);

        Assert.False(cap.Kick);
        Assert.Null(cap.TargetKind);
    }

    [Fact]
    public void Describe_NullBlueprint_ClaimsNothing()
    {
        ModerationCapability cap = ModerationTargetResolver.Describe((Blueprint?)null);

        Assert.False(cap.Kick);
        Assert.Null(cap.TargetKind);
    }

    /// <summary>
    /// A blueprint and the instance installed from it declare the same templates, so the catalog and
    /// the running server have to report the same capability — the case where they could drift is a
    /// second derivation, which is what the shared one exists to prevent.
    /// </summary>
    [Fact]
    public void Describe_BlueprintAndItsInstance_Agree()
    {
        var blueprint = new Blueprint
        {
            Name = "factorio",
            KickCommand = "/kick {name}",
            BanCommand = "/ban {name}",
        };
        // A blueprint spells an undeclared command null and an instance spells it empty, which is the
        // one difference between the two sources and exactly what the shared derivation absorbs.
        var instance = new Instance
        {
            KickCommand = blueprint.KickCommand ?? string.Empty,
            BanCommand = blueprint.BanCommand ?? string.Empty,
            UnbanCommand = blueprint.UnbanCommand ?? string.Empty,
        };

        ModerationCapability fromBlueprint = ModerationTargetResolver.Describe(blueprint);

        Assert.Equal(ModerationTargetResolver.Describe(instance), fromBlueprint);
        Assert.True(fromBlueprint.Kick);
        Assert.True(fromBlueprint.Ban);
        Assert.False(fromBlueprint.Unban);
        Assert.Equal("name", fromBlueprint.TargetKind);
    }
}
