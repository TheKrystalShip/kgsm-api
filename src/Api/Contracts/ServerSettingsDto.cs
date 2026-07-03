namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// Settings for one server instance. Phase 0: <see cref="AutoUpdate"/>. Phase 1 adds
/// <see cref="Autostart"/>. Phase 2 adds <see cref="CpuPriority"/> + <see cref="MemoryCapMb"/>. Phase 3 adds
/// the schedule: <see cref="ScheduledRestart"/>/<see cref="RestartTime"/>/<see cref="RestartDay"/>/
/// <see cref="Timezone"/> (from kgsm instance config) + <see cref="NextFireUtc"/> (computed by the scheduler
/// leaf, read from its status socket — null when the scheduler is absent/unreachable).
/// Typed façade over kgsm config + watchdog desired-state — never fabricated: a field is <c>null</c>
/// when its backing authority is absent/unreachable (or the kgsm config key is unset), not defaulted
/// to a guess.
/// </summary>
public sealed record ServerSettings(
    string ServerId,
    bool AutoUpdate,
    bool? Autostart,
    string? CpuPriority,
    int? MemoryCapMb,
    // Phase 3 — schedule config (values from kgsm config; nextFireUtc from the scheduler status socket).
    string? ScheduledRestart,
    string? RestartTime,
    string? RestartDay,
    string? Timezone,
    DateTimeOffset? NextFireUtc);

/// <summary>
/// PATCH body for <c>PATCH /servers/{id}/settings</c>. Sparse: only non-null fields are applied.
/// Phase 0 <see cref="AutoUpdate"/> → kgsm <c>auto_update</c> key.
/// Phase 1 <see cref="Autostart"/> → watchdog enable/disable (boot-autostart set).
/// Phase 2 <see cref="CpuPriority"/> (low/normal/high) → kgsm <c>cpu_priority</c> key + best-effort
/// watchdog live-apply; <see cref="MemoryCapMb"/> (≥0, 0=uncapped) → kgsm <c>memory_cap_mb</c> key
/// (takes effect at next restart).
/// Phase 3 the schedule keys → kgsm config: <see cref="ScheduledRestart"/> (off/daily/weekly/6h) →
/// <c>scheduled_restart</c>; <see cref="RestartTime"/> (HH:MM) → <c>restart_time</c>;
/// <see cref="RestartDay"/> (sun…sat) → <c>restart_day</c>; <see cref="Timezone"/> (IANA) → <c>timezone</c>.
/// The scheduler leaf re-reads kgsm config as its source of truth — the API only persists, never pushes.
/// </summary>
public sealed record ServerSettingsPatch(
    bool? AutoUpdate,
    bool? Autostart,
    string? CpuPriority,
    int? MemoryCapMb,
    // Phase 3 — schedule
    string? ScheduledRestart,
    string? RestartTime,
    string? RestartDay,
    string? Timezone,
    string? Origin = null);

/// <summary>
/// The <c>PATCH /servers/{id}/settings</c> success body: the camelCase field names that were applied, plus
/// the fresh post-write settings (so the client need not re-GET). Returned on a fully-applied <c>200</c>.
/// </summary>
public sealed record ServerSettingsApplied(IReadOnlyList<string> Applied, ServerSettings Settings);
