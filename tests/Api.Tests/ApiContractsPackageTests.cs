using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using TheKrystalShip.Api.Contracts;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// The wire contract travels as a package, and these are what stop it travelling wrong.
/// </summary>
/// <remarks>
/// <para>
/// A consumer on another machine compiles against these records and reads bytes this API wrote. Two
/// things can silently break that and neither shows up in a build: the serialization the package ships
/// disagreeing with the serialization the API actually uses, and a root nobody registered — which
/// throws only when somebody asks for it, on the machine that asked.
/// </para>
/// <para>
/// Both are checked here rather than in kgsm-api's controllers, because the thing under test is the
/// package's promise, not any one route's behaviour.
/// </para>
/// </remarks>
public sealed class ApiContractsPackageTests
{
    /// <summary>
    /// The reflection-based options this API's controllers serialize with, and the source-generated
    /// context the package hands a consumer, must produce the same bytes. They are two configurations
    /// of one contract and nothing but this holds them together.
    /// </summary>
    [Fact]
    public void The_shipped_context_and_the_api_options_agree()
    {
        var options = new JsonSerializerOptions();
        ApiJson.Configure(options);

        var job = new Job(
            "job_1", "factorio", "start", "succeeded",
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(2)),
            new DateTimeOffset(2026, 1, 2, 3, 4, 9, TimeSpan.Zero),
            Error: null);

        string byOptions = JsonSerializer.Serialize(job, options);
        string byContext = JsonSerializer.Serialize(job, ApiContractsJson.Default.Job);

        Assert.Equal(byOptions, byContext);
    }

    /// <summary>
    /// camelCase names and a <c>Z</c>-suffixed UTC timestamp, spelled out rather than asserted through
    /// the policy that produces them — the policy is what a consumer would otherwise have to guess, so
    /// a test that reads it back from the same policy would prove nothing.
    /// </summary>
    [Fact]
    public void The_wire_form_is_camel_case_and_utc()
    {
        var job = new Job(
            "job_1", "factorio", "start", "succeeded",
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(2)),
            SettledAt: null, Error: null);

        string json = JsonSerializer.Serialize(job, ApiContractsJson.Default.Job);

        Assert.Contains("\"serverId\":\"factorio\"", json);
        // Round-trip "O", so the fraction is always written — a consumer parsing this reads any
        // ISO-8601 form, and what matters here is that the instant is in UTC and says so.
        Assert.Contains("\"createdAt\":\"2026-01-02T01:04:05.0000000Z\"", json);
        Assert.DoesNotContain("ServerId", json);
        Assert.DoesNotContain("+02:00", json);
    }

    /// <summary>
    /// A source-generated context has no reflection fallback, so a root left unregistered throws the
    /// first time a consumer reads it — at run time, on their machine. Every root this contract is read
    /// through is asserted present here so that failure happens in this build instead.
    /// </summary>
    [Theory]
    [InlineData(typeof(List<Server>))]
    [InlineData(typeof(Server))]
    [InlineData(typeof(CommandRequest))]
    [InlineData(typeof(CommandAccepted))]
    [InlineData(typeof(Job))]
    [InlineData(typeof(InstallRequest))]
    [InlineData(typeof(DirListingDto))]
    [InlineData(typeof(FileContentDto))]
    [InlineData(typeof(FileFindDto))]
    [InlineData(typeof(FileSearchDto))]
    [InlineData(typeof(SaveFileRequest))]
    [InlineData(typeof(SaveFileResultDto))]
    [InlineData(typeof(ConsoleScrollback))]
    [InlineData(typeof(ServerConfig))]
    [InlineData(typeof(ServerConfigPatch))]
    [InlineData(typeof(ServerSettings))]
    [InlineData(typeof(ServerSettingsPatch))]
    [InlineData(typeof(ServerBackupList))]
    [InlineData(typeof(CreateBackupRequest))]
    [InlineData(typeof(RestoreBackupRequest))]
    [InlineData(typeof(PruneBackupsRequest))]
    [InlineData(typeof(PlayersResponse))]
    [InlineData(typeof(ModerationResult))]
    [InlineData(typeof(List<Host>))]
    [InlineData(typeof(HostPortsDto))]
    [InlineData(typeof(List<LibraryEntry>))]
    [InlineData(typeof(AuditPage))]
    [InlineData(typeof(ErrorEnvelope))]
    public void Every_root_a_caller_reads_is_registered(Type root)
    {
        JsonTypeInfo? info = ApiContractsJson.Default.GetTypeInfo(root);

        Assert.NotNull(info);
    }

    /// <summary>
    /// The package carries the shapes and nothing else. A contract type reaching for something in the
    /// API's own assembly would not compile for a consumer at all — which is the failure this catches
    /// before a publish rather than after one.
    /// </summary>
    [Fact]
    public void The_package_depends_on_nothing_of_this_apis_own()
    {
        Assembly contracts = typeof(Server).Assembly;

        Assert.DoesNotContain(
            contracts.GetReferencedAssemblies(),
            a => a.Name is not null && a.Name.StartsWith("kgsm-api", StringComparison.Ordinal));
    }
}
