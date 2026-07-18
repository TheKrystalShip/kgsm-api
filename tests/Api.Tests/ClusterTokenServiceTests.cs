using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TheKrystalShip.Api.Services.Cluster;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Unit tests for <see cref="ClusterTokenService"/> — the cluster message bus's node-to-node auth
/// seam (Phase 1 foundation, <c>docs/cluster-message-bus-plan.md</c> / <c>PLAN-peers.md §3</c>).
/// Constructed directly (no <c>WebApplicationFactory</c>) since the service only depends on
/// <see cref="ApiOptions"/> + a logger, the same pattern as <see cref="AuthTiersResolveTests"/>.
/// </summary>
public sealed class ClusterTokenServiceTests
{
    private static ApiOptions Options(
        string secret = "cluster-secret",
        string previousSecret = "",
        string nodeId = "node-a") => new()
    {
        HostId = "test", HostLabel = "test",
        MonitorSocketPath = "", WatchdogSocketPath = "", AssistantBaseUrl = "", AssistantRelaySecret = "",
        FirewallSocketPath = "", SchedulerSocketPath = "", KgsmPath = "/usr/bin/kgsm", KgsmSocketPath = "",
        BlueprintCacheTtlSeconds = 60,
        InstanceCacheTtlSeconds = 60,
        LogSources = [], JournalctlPath = "journalctl", SystemctlPath = "systemctl", LogReadTimeoutMs = 5000,
        RawgApiKey = "", RawgCacheDir = "covers", PublicBaseUrl = "",
        SteamCdnBaseUrl = "https://steamcdn.test/apps",
        LibraryRefreshIntervalDays = 7, LibraryRefreshHour = 6,
        FilesMaxEntries = 200, FilesMaxEditBytes = 2 * 1024 * 1024,
        LeafOverridesDir = "/tmp/kgsm-api-test-overrides", LeafApplyCanaryMs = 15000,
        DomainPollMs = 5000, MetricsPollMs = 1000, ServicesPollMs = 5000,
        AuthDisabled = false, SigningKey = "", DiscordClientId = "", DiscordClientSecret = "",
        DiscordRedirectUri = "", DiscordBotToken = "", DiscordGuildId = "", AuthFrontendUrl = "",
        RoleAdminIds = [], RoleOperatorIds = [], RoleViewerIds = [],
        SessionsCacheTtlMs = 5000, SessionsGcMs = 600000, SessionsRefreshAbsoluteDays = 30,
        ClusterSecret = secret, ClusterSecretPrevious = previousSecret, NodeId = nodeId,
    };

    private static ClusterTokenService Service(string secret = "cluster-secret", string previousSecret = "", string nodeId = "node-a") =>
        new(Options(secret, previousSecret, nodeId), NullLogger<ClusterTokenService>.Instance);

    /// <summary>
    /// Hand-mints a token with the SAME crypto idiom as <see cref="ClusterTokenService"/> (SHA-256
    /// derived HMAC key), but with arbitrary claims/lifetime — used to exercise negative paths
    /// (wrong audience, expired) the service's own <c>Mint()</c> cannot produce (it always mints a
    /// well-formed, 60s-TTL, <c>aud=cluster</c> token).
    /// </summary>
    private static string MintCustom(string secret, string issuer, string audience, TimeSpan ttl, DateTime? issuedAt = null)
    {
        var key = new SymmetricSecurityKey(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
        var signing = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        DateTime iat = issuedAt ?? DateTime.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Subject = new ClaimsIdentity([new Claim("sub", $"node:{issuer}")]),
            IssuedAt = iat,
            NotBefore = iat,
            Expires = iat.Add(ttl),
            SigningCredentials = signing,
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    [Fact]
    public async Task MintThenValidate_RoundTrips_ReturnsMintingNodeId()
    {
        ClusterTokenService svc = Service(nodeId: "node-a");

        MintedClusterToken minted = svc.Mint();
        ClusterPrincipal? principal = await svc.ValidateAsync(minted.Token);

        Assert.NotNull(principal);
        Assert.Equal("node-a", principal!.NodeId);
    }

    [Fact]
    public void Mint_SetsAShortAbsoluteExpiry()
    {
        ClusterTokenService svc = Service();
        DateTimeOffset before = DateTimeOffset.UtcNow;

        MintedClusterToken minted = svc.Mint();

        // 60s TTL — allow generous slack for CI scheduling jitter, but it must not be a long-lived
        // (e.g. 15-min user-access-token-shaped) TTL.
        Assert.True(minted.ExpiresAt > before);
        Assert.True(minted.ExpiresAt <= before.AddSeconds(90));
    }

    [Fact]
    public async Task TokenSignedWithPreviousSecret_ValidatesDuringRotationWindow()
    {
        // Node mints under the OLD secret (hasn't rotated yet)...
        ClusterTokenService minter = Service(secret: "old-secret", nodeId: "node-b");
        MintedClusterToken minted = minter.Mint();

        // ...the receiver has already rotated to a NEW secret, but still accepts the old one during
        // the overlap window (PLAN-peers.md §2 #9).
        ClusterTokenService receiver = Service(secret: "new-secret", previousSecret: "old-secret", nodeId: "node-b");

        ClusterPrincipal? principal = await receiver.ValidateAsync(minted.Token);

        Assert.NotNull(principal);
        Assert.Equal("node-b", principal!.NodeId);
    }

    [Fact]
    public async Task WrongSecret_FailsClosed_ReturnsNull()
    {
        ClusterTokenService minter = Service(secret: "secret-one");
        MintedClusterToken minted = minter.Mint();

        // No shared secret at all with the minter, and no previous secret configured either.
        ClusterTokenService receiver = Service(secret: "totally-different-secret");

        Assert.Null(await receiver.ValidateAsync(minted.Token));
    }

    [Fact]
    public async Task GarbageToken_FailsClosed_ReturnsNull()
    {
        ClusterTokenService svc = Service();

        Assert.Null(await svc.ValidateAsync("not-a-jwt-at-all"));
    }

    [Fact]
    public async Task ExpiredToken_FailsClosed_ReturnsNull()
    {
        const string secret = "expiry-secret";
        // Signed correctly, but already expired at mint time (issued an hour ago with a 1-minute TTL).
        string expired = MintCustom(secret, issuer: "node-e", audience: "cluster",
            ttl: TimeSpan.FromMinutes(1), issuedAt: DateTime.UtcNow.AddHours(-1));

        ClusterTokenService receiver = Service(secret: secret);

        Assert.Null(await receiver.ValidateAsync(expired));
    }

    [Fact]
    public async Task WrongAudience_FailsClosed_ReturnsNull()
    {
        const string secret = "aud-secret";
        // Correctly signed and not expired, but aud != "cluster".
        string wrongAudience = MintCustom(secret, issuer: "node-f", audience: "not-cluster", ttl: TimeSpan.FromSeconds(60));

        ClusterTokenService receiver = Service(secret: secret);

        Assert.Null(await receiver.ValidateAsync(wrongAudience));
    }

    [Fact]
    public void BlankClusterSecret_MintThrows()
    {
        ClusterTokenService svc = Service(secret: "");

        Assert.Throws<InvalidOperationException>(() => svc.Mint());
    }

    [Fact]
    public async Task BlankClusterSecret_ValidateReturnsNull()
    {
        // Mint a real token from an ENABLED node, then confirm a disabled node (blank secret) never
        // validates anything — including a token that would otherwise be perfectly valid elsewhere.
        ClusterTokenService enabled = Service(secret: "some-secret", nodeId: "node-d");
        MintedClusterToken minted = enabled.Mint();

        ClusterTokenService disabled = Service(secret: "");

        Assert.Null(await disabled.ValidateAsync(minted.Token));
    }
}
