namespace TheKrystalShip.Api.Realtime;

/// <summary>
/// The frozen server→client envelope (<c>architecture.html §3·b</c>): every pushed message is
/// <c>{ topic, type, data }</c> and is scoped to the host whose socket delivered it. <see cref="Data"/>
/// is typed <see cref="object"/> so System.Text.Json serializes it by its <em>runtime</em> type — the
/// honest DTO for that message (a <c>Server</c>, <c>ServerMetricsDto</c>, <c>HostMetricsDto</c>,
/// <c>HostCapabilities</c>, or <c>ServerRemoved</c>) — with the same camelCase/'Z' shaping as the REST
/// surface, so a WS patch is byte-identical to the REST element it patches.
/// </summary>
public sealed record StreamMessage(string Topic, string Type, object Data);
