namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// The editable runtime configuration of one instance (Tier-1 ops — <c>GET /servers/{id}/config</c>). The
/// SPA's settings panel reads this to render the per-server config form and writes it back through
/// <c>PATCH /servers/{id}/config</c>.
/// <para>
/// <see cref="Values"/> is a map keyed by kgsm's own <strong>snake_case</strong> config keys
/// (<c>auto_update</c>, <c>executable_arguments</c>, <c>level_name</c>, …). The snake_case here is
/// <em>data</em>, not a DTO-property name — these are the engine's domain identifiers and a subsequent PATCH
/// must echo them back <em>verbatim</em> to <c>config-set</c>, so renaming them to camelCase would break the
/// round-trip. (The camelCase idiom governs DTO property names like <see cref="Values"/>, not map keys.)
/// </para>
/// <para>
/// The map carries only the keys kgsm permits editing — the complement of its protected set (identity/path
/// keys <c>name</c>/<c>runtime</c>/<c>ports</c>/every <c>*_dir</c>/<c>*_file</c>/… and the integration toggles
/// <c>enable_firewall_management</c>/<c>enable_command_shortcuts</c>, which have dedicated flows). Values are
/// the engine's current values, stringified to what <c>config-set</c> accepts (booleans as
/// <c>"true"</c>/<c>"false"</c>). Never fabricated: a value the engine reports empty is the empty string,
/// never a guessed default.
/// </para>
/// </summary>
public sealed record ServerConfig(string ServerId, IReadOnlyDictionary<string, string> Values);

/// <summary>
/// The request body for <c>PATCH /servers/{id}/config</c> (Tier-1 ops). Applies one-or-more
/// <c>key=value</c> assignments to the instance config via kgsm-lib's <c>SetInstanceConfigValue</c>.
/// <list type="bullet">
///   <item><description><see cref="Values"/> — the keys to set, by the engine's snake_case identifiers
///     (the same shape <c>GET /servers/{id}/config</c> returns). At least one entry is required.</description></item>
///   <item><description><see cref="Origin"/> — the driving surface (like <see cref="CommandRequest.Origin"/>),
///     stamped onto each engine call so the resulting config write is attributable. Absent ⇒ <c>api</c>.</description></item>
/// </list>
/// <para>
/// The API validates every key against the editable set BEFORE applying any (a strictly-stricter pre-check,
/// not a bypass of the engine's own refusal): if any key is protected/unknown the request is rejected
/// <c>400</c> with nothing applied. The engine remains the final authority — a key that passes the pre-check
/// but the engine still refuses surfaces the engine's real <c>4xx</c> detail. Config writes are NOT
/// transactional (kgsm <c>config-set</c> is per-key), so a mid-apply engine refusal reports which keys had
/// already been applied rather than feigning atomicity.
/// </para>
/// </summary>
public sealed record ServerConfigPatch(
    IReadOnlyDictionary<string, string>? Values,
    string? Origin = null);

/// <summary>
/// The <c>PATCH /servers/{id}/config</c> success body: the keys that were applied, plus the fresh
/// post-write config (so the client need not re-GET). Returned on a fully-applied <c>200</c>.
/// </summary>
public sealed record ServerConfigApplied(IReadOnlyList<string> Applied, ServerConfig Config);
