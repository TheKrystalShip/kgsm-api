using System.Text.Json.Serialization;

namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// The wire form of this contract, reflection-free.
/// </summary>
/// <remarks>
/// <para>
/// <b>The names are the contract and they are not on the records.</b> A property's wire name comes
/// from a naming policy rather than an attribute, so a consumer holding only the records would be
/// holding types whose correctness depends on it configuring the same policy — which nothing checks
/// and nothing fails on. Shipping the serialization beside the shapes is what makes the package a
/// contract rather than a set of class definitions.
/// </para>
/// <para>
/// <b>Every root a caller reads is registered here.</b> A source-generated context has no reflection
/// fallback: a type that is not registered throws at the first attempt to serialize it, which is loud
/// and immediate rather than silent. Nested types come along on their own, so only the roots are
/// listed. Adding a route a member calls means adding its root here.
/// </para>
/// <para>
/// <see cref="ApiJson"/> configures the same policy for a reflection-based pipeline, which is what
/// kgsm-api's own controllers use. The two are held to each other by test rather than by convention.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    Converters = [typeof(Iso8601UtcDateTimeOffsetConverter)])]
// Servers, their lifecycle and what a command did.
[JsonSerializable(typeof(List<Server>))]
[JsonSerializable(typeof(Server))]
[JsonSerializable(typeof(CommandRequest))]
[JsonSerializable(typeof(CommandAccepted))]
[JsonSerializable(typeof(Job))]
[JsonSerializable(typeof(InstallRequest))]
// An instance's files.
[JsonSerializable(typeof(DirListingDto))]
[JsonSerializable(typeof(FileContentDto))]
[JsonSerializable(typeof(FileFindDto))]
[JsonSerializable(typeof(FileSearchDto))]
[JsonSerializable(typeof(SaveFileRequest))]
[JsonSerializable(typeof(SaveFileResultDto))]
// Its configuration and its settings.
[JsonSerializable(typeof(ServerConfig))]
[JsonSerializable(typeof(ServerConfigPatch))]
[JsonSerializable(typeof(ServerConfigApplied))]
[JsonSerializable(typeof(ServerSettings))]
[JsonSerializable(typeof(ServerSettingsPatch))]
// Its backups.
[JsonSerializable(typeof(ServerBackupList))]
[JsonSerializable(typeof(CreateBackupRequest))]
[JsonSerializable(typeof(RestoreBackupRequest))]
[JsonSerializable(typeof(PruneBackupsRequest))]
// Who is on it, and what may be done to them.
[JsonSerializable(typeof(PlayersResponse))]
[JsonSerializable(typeof(ModerationResult))]
// The host itself.
[JsonSerializable(typeof(List<Host>))]
[JsonSerializable(typeof(Host))]
[JsonSerializable(typeof(HostPortsDto))]
// What can be installed.
[JsonSerializable(typeof(List<LibraryEntry>))]
// What happened.
[JsonSerializable(typeof(AuditPage))]
// And the one shape every refusal arrives in.
[JsonSerializable(typeof(ErrorEnvelope))]
public sealed partial class ApiContractsJson : JsonSerializerContext;
