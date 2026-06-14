namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// Liveness payload for <c>GET /healthz</c>. This endpoint is <b>ours</b> (ops /
/// load-balancer / smoke probe), not a frontend contract — architecture.html
/// specifies no health endpoint; the SPA derives liveness from its data stores'
/// <c>connecting → live → down</c> state (§3·j), not from here. Returned as JSON
/// (not bare text) to stay consistent with the API's JSON-everywhere convention.
/// The <see cref="Time"/> serializes as ISO-8601 UTC <c>Z</c> via the global
/// <c>Iso8601UtcDateTimeOffsetConverter</c> (registered on JSON options in Program.cs).
/// </summary>
public sealed record HealthStatus(string Status, string Service, DateTimeOffset Time);
