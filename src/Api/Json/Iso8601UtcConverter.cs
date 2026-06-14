using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TheKrystalShip.Api.Json;

/// <summary>
/// Serializes a <see cref="DateTimeOffset"/> as ISO-8601 in UTC with a <c>Z</c>
/// suffix — the timestamp convention frozen in architecture.html §6 ("All timestamps
/// ISO-8601 UTC (Z)"). System.Text.Json's default emits the local offset form
/// (<c>+00:00</c>), which is valid ISO-8601 but not the contracted shape; this
/// normalizes it.
///
/// Registered once on the global JSON options (controllers' <c>AddJsonOptions</c> in
/// Program.cs), so it applies to every response uniformly.
/// </summary>
public sealed class Iso8601UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        // GetDateTimeOffset parses any ISO-8601 form (offset or Z); accept both inbound.
        => reader.GetDateTimeOffset();

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        // UtcDateTime has Kind=Utc, so "O" round-trip format ends in 'Z' (never the
        // ".Z" degenerate that a hand-rolled ".FFFZ" mask can produce on whole seconds).
        => writer.WriteStringValue(value.ToUniversalTime().UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
}
