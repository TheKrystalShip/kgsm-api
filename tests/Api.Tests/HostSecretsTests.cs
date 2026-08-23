using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Auth;

using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Users;

using Xunit;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// The two secrets a host generates for itself when nobody hands it one: the session signing key, and
/// the first administrator's password.
/// </summary>
public class HostSigningKeyTests : IDisposable
{
    private readonly string _stateDir =
        Path.Combine(Path.GetTempPath(), $"kgsm-api-signing-key-{Guid.NewGuid():N}");

    private ApiOptions Options(string? configured) =>
        ApiOptions.FromSettings(new ApiSettings
        {
            DbPath = Path.Combine(_stateDir, "kgsm-api.db"),
            SigningKey = configured,
        });

    private HostSigningKey Resolve(string? configured) =>
        new(Options(configured), NullLogger<HostSigningKey>.Instance);

    [Fact]
    public void BlankConfiguration_GeneratesAKeyAndKeepsIt()
    {
        HostSigningKey key = Resolve(null);

        Assert.Equal(Options(null).SigningKeyPath, key.FilePath);
        Assert.True(File.Exists(key.FilePath));
        Assert.Equal(key.Value, File.ReadAllText(key.FilePath!));

        // 48 random bytes, base64 — the same shape the env example tells an operator to generate.
        Assert.Equal(48, Convert.FromBase64String(key.Value).Length);
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public void TheGeneratedKeyFileIsReadableByNobodyElse()
    {
        HostSigningKey key = Resolve("");

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(key.FilePath!));
    }

    [Fact]
    public void ASecondStartReadsTheSameKey()
    {
        HostSigningKey first = Resolve(null);
        HostSigningKey second = Resolve(null);

        // The whole point: a restart does not sign everybody out.
        Assert.Equal(first.Value, second.Value);
    }

    [Fact]
    public void AKeyWrittenByHandIsUsedVerbatim()
    {
        Directory.CreateDirectory(_stateDir);
        // With a trailing newline, because a file an operator wrote has one and a key that silently
        // included it would validate none of the tokens minted before they edited it.
        File.WriteAllText(Options(null).SigningKeyPath, "written-by-an-operator\n");

        Assert.Equal("written-by-an-operator", Resolve(null).Value);
    }

    [Fact]
    public void AConfiguredKeyWinsAndNothingIsWritten()
    {
        HostSigningKey key = Resolve("a-real-host-was-given-a-secret");

        Assert.Equal("a-real-host-was-given-a-secret", key.Value);
        Assert.Null(key.FilePath);
        Assert.False(File.Exists(Options(null).SigningKeyPath));
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (Directory.Exists(_stateDir)) Directory.Delete(_stateDir, recursive: true);
    }
}

/// <summary>
/// The administrator a host with no accounts creates for itself on its first start, and the file its
/// password is left in. Run against the real pipeline, because the store, the file and the login that
/// consumes it are three parts of one behaviour.
/// </summary>
public class FirstAdminTests : IClassFixture<AuthTestFactory>
{
    private readonly AuthTestFactory _factory;
    private readonly ApiOptions _options;

    public FirstAdminTests(AuthTestFactory factory)
    {
        _factory = factory;
        // Forces the host to start, which is what runs the bootstrap.
        factory.CreateClient();
        _options = factory.Services.GetRequiredService<ApiOptions>();
    }

    [Fact]
    public async Task AHostWithNoAccountsGetsAnAdministrator()
    {
        IUserStore store = _factory.Services.GetRequiredService<UserDirectory>().Store;
        KgsmUser? admin = await store.FindByUsernameAsync(FirstAdmin.DefaultUsername);

        Assert.NotNull(admin);
        Assert.Equal(KgsmTier.Admin, admin!.Tier);
        Assert.Equal(UserStatus.Active, admin.Status);
        // Granted, not derived: this is the account the pending-account expiry must never reap.
        Assert.Equal(TierSource.Granted, admin.TierSource);
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public void ItsPasswordIsLeftWhereOnlyTheServiceUserCanReadIt()
    {
        Assert.True(File.Exists(_options.InitialAdminPasswordPath));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(_options.InitialAdminPasswordPath));
        Assert.Contains($"username: {FirstAdmin.DefaultUsername}",
            File.ReadAllText(_options.InitialAdminPasswordPath));
    }

    /// <summary>
    /// The end of the whole feature: what the file says gets somebody in, and the file goes away when
    /// it does. Its own factory, because signing in is what deletes the file the other tests read.
    /// </summary>
    [Fact]
    public async Task ThePasswordInTheFileSignsIn_AndConsumesIt()
    {
        using AuthTestFactory factory = new();
        HttpClient client = factory.CreateClient();
        ApiOptions options = factory.Services.GetRequiredService<ApiOptions>();

        string password = File.ReadAllLines(options.InitialAdminPasswordPath)
            .First(l => l.StartsWith("password:", StringComparison.Ordinal))["password:".Length..].Trim();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/auth/login", new { username = FirstAdmin.DefaultUsername, password });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        LoginResult? session = await response.Content.ReadFromJsonAsync<LoginResult>();
        Assert.Equal("admin", session!.Tier);

        Assert.False(File.Exists(options.InitialAdminPasswordPath));
    }

    /// <summary>A host that already has accounts is left entirely alone.</summary>
    [Fact]
    public async Task AHostThatAlreadyHasAccountsIsLeftAlone()
    {
        UserDirectory users = _factory.Services.GetRequiredService<UserDirectory>();

        Assert.Null(await FirstAdmin.CreateAsync(
            users.Store, users.SignIn, "someone-else"));
        Assert.Null(await users.Store.FindByUsernameAsync("someone-else"));
    }
}
