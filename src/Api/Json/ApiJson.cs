using System.Text.Json;

namespace TheKrystalShip.Api.Json;

/// <summary>
/// One place that configures JSON the same way for every serialization path — MVC
/// controllers (<c>AddControllers().AddJsonOptions</c>) and the minimal/HTTP path
/// (<c>ConfigureHttpJsonOptions</c>, used by <c>WriteAsJsonAsync</c>), which are
/// otherwise separate options objects. Keeps the wire shape consistent whether a
/// response comes from a controller or the error writer.
///
/// Conventions frozen from architecture.html §6: camelCase property names and
/// ISO-8601 UTC <c>Z</c> timestamps. Note we deliberately do NOT set a global
/// "ignore nulls" — domain DTOs (M1) use explicit <c>null</c> to mean "unknown /
/// source absent", which must survive to the wire; per-field omission is opt-in via
/// <c>[JsonIgnore(WhenWritingNull)]</c> (e.g. <c>ErrorBody.Details</c>).
/// </summary>
public static class ApiJson
{
    public static void Configure(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.Converters.Add(new Iso8601UtcDateTimeOffsetConverter());
    }
}
