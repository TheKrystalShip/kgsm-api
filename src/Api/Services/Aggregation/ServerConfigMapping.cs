using System.Globalization;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Api.Services.Aggregation;

/// <summary>
/// Pure mapping between a kgsm <see cref="Instance"/> and the editable-config view the SPA reads/writes
/// (Tier-1 ops — <c>GET</c>/<c>PATCH /servers/{id}/config</c>). No I/O — unit-testable in isolation. The
/// load-bearing correctness surface is the <see cref="IsEditableKey"/> boundary: it must be the exact
/// complement of kgsm's <c>__is_protected_instance_config_key</c> (kgsm-lib's <c>config-set</c> refuses the
/// protected set), so the API never offers a key the engine will refuse, and never hides one it would accept.
/// </summary>
/// <remarks>
/// kgsm's protected set (engine source <c>commands/handlers/instances.sh</c> §<c>__is_protected_instance_config_key</c>):
/// <c>name</c>, <c>blueprint_file</c>, <c>runtime</c>, <c>platform</c>, <c>install_datetime</c>,
/// <c>is_steam_account_required</c>, <c>steam_app_id</c>, <c>client_steam_app_id</c>, <c>ports</c>; every
/// <c>*_dir</c> / <c>*_file</c> and <c>executable_subdirectory</c>; and the integration toggles
/// <c>enable_firewall_management</c> / <c>enable_command_shortcuts</c>. Everything else is a plain runtime
/// value and is editable. Mirrored here as the predicate the engine enforces, NOT a hand-picked allowlist,
/// so a key kgsm later relaxes does not silently stay hidden (the projection below is the explicit set the
/// API surfaces today, but the PATCH gate uses this predicate so it can never bypass the engine refusal).
/// </remarks>
public static class ServerConfigMapping
{
    /// <summary>
    /// Whether <paramref name="key"/> is editable via <c>config-set</c> — the complement of kgsm's protected
    /// set. The protected predicate is replicated verbatim from the engine; a key kgsm refuses must return
    /// false here so the PATCH gate rejects it up front (a stricter pre-check, never a bypass).
    /// </summary>
    public static bool IsEditableKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return false;

        // The management script refuses to source a config with any non-identifier key, so a malformed key
        // is never editable (it would brick the instance). Mirrors kgsm's ^[a-zA-Z_][a-zA-Z0-9_]*$ guard.
        if (!IsValidIdentifier(key))
            return false;

        return !IsProtectedKey(key);
    }

    // The exact mirror of kgsm's __is_protected_instance_config_key (engine authority).
    private static bool IsProtectedKey(string key)
    {
        switch (key)
        {
            case "name":
            case "blueprint_file":
            case "runtime":
            case "platform":
            case "install_datetime":
            case "is_steam_account_required":
            case "steam_app_id":
            case "client_steam_app_id":
            case "ports":
            case "executable_subdirectory":
            case "enable_firewall_management":
            case "enable_command_shortcuts":
                return true;
        }

        // every *_dir / *_file path key kgsm manages
        return key.EndsWith("_dir", StringComparison.Ordinal)
            || key.EndsWith("_file", StringComparison.Ordinal);
    }

    private static bool IsValidIdentifier(string key)
    {
        if (!(char.IsAsciiLetter(key[0]) || key[0] == '_'))
            return false;
        foreach (char c in key)
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_'))
                return false;
        return true;
    }

    /// <summary>
    /// Project an <see cref="Instance"/> to its editable config map (the <c>GET</c> body's <c>values</c>),
    /// keyed by kgsm's snake_case keys. Each value is the engine's current value, stringified to what
    /// <c>config-set</c> accepts: booleans as <c>"true"</c>/<c>"false"</c> (the doc's example form, not
    /// <c>0</c>/<c>1</c>), ints in invariant culture. Values are surfaced verbatim — an empty string stays
    /// empty (e.g. cleared <c>executable_arguments</c>), never coerced to a fabricated default.
    /// <para>
    /// This is a <em>curated</em> set, not the exhaustive editable complement: PATCH accepts ANY non-protected
    /// key (kgsm <c>config-set</c> has only a protected denylist, no allowlist), but GET projects the keys the
    /// SPA's settings form needs. Deliberately <strong>excluded</strong>: <c>enable_port_forwarding</c> — kgsm
    /// stripped UPnP from the bash engine (re-homed into the watchdog) so it is not emitted on the wire today;
    /// projecting it would always read the safe-default <c>false</c> and never reflect a write, so it is held
    /// out until that migration re-supplies the field (no value the engine cannot round-trip).
    /// </para>
    /// </summary>
    public static ServerConfig ToConfig(Instance instance)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["auto_update"] = Bool(instance.AutoUpdate),
            ["executable_arguments"] = instance.ExecutableArguments ?? "",
            ["level_name"] = instance.LevelName ?? "",
            ["stop_command"] = instance.StopCommand ?? "",
            ["save_command"] = instance.SaveCommand ?? "",
            ["save_command_timeout_seconds"] = Int(instance.SaveCommandTimeoutSeconds),
            ["stop_command_timeout_seconds"] = Int(instance.StopCommandTimeoutSeconds),
            ["startup_success_regex"] = instance.StartupSuccessRegex ?? "",
            ["player_joined_regex"] = instance.PlayerJoinedRegex ?? "",
            ["player_left_regex"] = instance.PlayerLeftRegex ?? "",
            ["compress_backups"] = Bool(instance.CompressBackups),
        };

        return new ServerConfig(instance.Name, values);
    }

    private static string Bool(bool b) => b ? "true" : "false";

    private static string Int(int i) => i.ToString(CultureInfo.InvariantCulture);
}
