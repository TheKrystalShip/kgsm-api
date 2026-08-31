using TheKrystalShip.Api.Services.Leaves;

namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// The scheduler leaf's whole board — every instance it supervises, with the cadence it was configured
/// with and the outcome of what it last ran. The per-server settings surface reads ONE row of this same
/// snapshot; this serves it entire, because "what is due next across this host" is a question no
/// per-server view can answer.
/// <para>
/// Relayed as the leaf reports it. Every computed field (each window's <c>nextFireUtc</c>) is the
/// scheduler's own arithmetic over its own clock — this API re-derives nothing, so the panel and the leaf
/// can never disagree about when something fires. A null is the leaf's honest "not scheduled" or "hasn't
/// run yet", never a gap this API filled in.
/// </para>
/// <para>
/// It stays with this API rather than travelling in the shared contract package, because the shape it
/// carries is the scheduler leaf's own and this API only relays it. A package declaring it would make
/// kgsm-api the owner of a wire contract it does not control and cannot keep in step.
/// </para>
/// </summary>
public sealed record SchedulerBoard(IReadOnlyList<SchedulerInstanceStatus> Data);
