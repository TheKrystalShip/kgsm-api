using TheKrystalShip.Api.Services.Auth;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Unit tests for <see cref="AuthTiers.Resolve"/> — the role→tier verdict at the heart of the auth
/// gate. The security-critical contract (set by user directive): <strong>guild membership is the
/// access gate</strong>; a non-member (null role list, from the lookup's 404) is <c>None</c> → 403,
/// while a verified member <strong>floors at Viewer</strong> and the Admin/Operator role ids elevate.
/// (These run below the <see cref="FakeDiscordResolver"/> seam the integration tests use, so they pin
/// the mapping itself rather than the HTTP pipeline.)
/// </summary>
public sealed class AuthTiersResolveTests
{
    private const string AdminRole = "1520175931828867193";
    private const string OpsRole = "1520175983880179804";

    private static ApiOptions Options() => new()
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
        DomainPollMs = 5000, MetricsPollMs = 1000,
        MetricsHistoryDb = "metrics.db", MetricsPersistMs = 15000, MetricsRawRetentionHours = 24,
        MetricsRollupStepMin = 5, MetricsRollupRetentionDays = 30, MetricsMaintenanceMs = 60000,
        AuthDisabled = false, SigningKey = "", DiscordClientId = "", DiscordClientSecret = "",
        DiscordRedirectUri = "", DiscordBotToken = "", DiscordGuildId = "", AuthFrontendUrl = "",
        RoleAdminIds = [AdminRole], RoleOperatorIds = [OpsRole], RoleViewerIds = [],
    };

    [Fact]
    public void NotAMember_IsNone()
    {
        // null = the 404 from the guild-member lookup = not in the guild = locked out (403).
        Assert.Equal(AuthTier.None, AuthTiers.Resolve(null, Options()));
    }

    [Fact]
    public void Member_WithNoMatchingRole_FloorsToViewer()
    {
        Assert.Equal(AuthTier.Viewer, AuthTiers.Resolve(["999", "888"], Options()));
    }

    [Fact]
    public void Member_WithEmptyRoles_FloorsToViewer()
    {
        // @everyone-only member: an empty role array is still a member → Viewer (NOT None).
        Assert.Equal(AuthTier.Viewer, AuthTiers.Resolve([], Options()));
    }

    [Fact]
    public void AdminRole_IsAdmin()
    {
        Assert.Equal(AuthTier.Admin, AuthTiers.Resolve([AdminRole], Options()));
    }

    [Fact]
    public void OpsRole_IsOperator()
    {
        Assert.Equal(AuthTier.Operator, AuthTiers.Resolve([OpsRole], Options()));
    }

    [Fact]
    public void AdminWins_OverOperator()
    {
        // Holding both → the highest tier (admin ⊇ operator).
        Assert.Equal(AuthTier.Admin, AuthTiers.Resolve([OpsRole, AdminRole], Options()));
    }
}
