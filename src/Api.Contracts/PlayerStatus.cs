using System.Text.Json.Serialization;

namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// The closed status vocabulary for a player's roster row. Serialized as lowercase JSON
/// via <c>[JsonStringEnumConverter&lt;PlayerStatus&gt;]</c> — <c>"online"</c>, <c>"offline"</c>, <c>"banned"</c>, <c>"unknown"</c>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<PlayerStatus>))]
public enum PlayerStatus
{
    online,
    offline,
    banned,
    unknown
}
