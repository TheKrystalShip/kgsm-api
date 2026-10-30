using System.Reflection;

using ActionIds = TheKrystalShip.Api.Services.Auth.ActionIds;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// What a test's caller may do: nothing, a reader, somebody running servers, or an Owner. Each is the
/// list of actions the access token carries.
/// </summary>
public enum Persona
{
    /// <summary>A token listing no action.</summary>
    None,

    /// <summary>The reads the panel's pages sit on.</summary>
    Reader,

    /// <summary>The reads and everything that runs a server: lifecycle, console, files, backups, players.</summary>
    Runner,

    /// <summary>Every action this API gates on.</summary>
    Owner,
}

/// <summary>The actions each <see cref="Persona"/> holds.</summary>
internal static class Personas
{
    /// <summary>The reads <see cref="Persona.Reader"/> holds.</summary>
    public static readonly IReadOnlySet<string> ReaderActions = new HashSet<string>(StringComparer.Ordinal)
    {
        ActionIds.ServerRead, ActionIds.ServerConfigRead, ActionIds.ServerConsoleRead, ActionIds.ServerBackupsRead,
        ActionIds.LibraryRead, ActionIds.HostsRead, ActionIds.BatchesRead, ActionIds.AlertsRead,
        ActionIds.AuditRead, ActionIds.MembersRead, ActionIds.MonitorMetricsRead,
    };

    /// <summary>What <see cref="Persona.Runner"/> holds on top of the reads.</summary>
    public static readonly IReadOnlySet<string> RunnerActions = new HashSet<string>(ReaderActions, StringComparer.Ordinal)
    {
        ActionIds.ServerStart, ActionIds.ServerStop, ActionIds.ServerRestart, ActionIds.ServerUpdate,
        ActionIds.ServerInstall, ActionIds.ServerUninstall, ActionIds.ServerConfigWrite, ActionIds.ServerWindowsWrite,
        ActionIds.ServerConsoleWrite, ActionIds.ServerFilesRead, ActionIds.ServerFilesWrite,
        ActionIds.ServerBackupsCreate, ActionIds.ServerBackupsRestore, ActionIds.ServerBackupsManage,
        ActionIds.ServerPlayersKick, ActionIds.ServerPlayersBan, ActionIds.ServerAnnounce,
        ActionIds.BatchesCancel, ActionIds.AuditPersonalFields, ActionIds.ServicesRead, ActionIds.LogsRead,
        ActionIds.EngineConfigRead, ActionIds.FirewallRulesRead, ActionIds.MonitorThresholdsRead,
        ActionIds.SchedulerWindowsRead, ActionIds.SchedulerWindowsWrite, ActionIds.WatchdogSupervisionRead,
        ActionIds.BotStatusRead, ActionIds.ReactorRulesRead,
    };

    /// <summary>The leaves whose configuration an Owner may read and write.</summary>
    private static readonly string[] Leaves =
        ["watchdog", "monitor", "firewall", "scheduler", "reactor", "api", "bot", "dns", "assistant", "weatherwatch"];

    /// <summary>Every action this API gates on, and the configuration actions of every leaf.</summary>
    public static readonly IReadOnlySet<string> AllActions = new HashSet<string>(
        typeof(ActionIds).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f is { IsLiteral: true } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .Concat(Leaves.SelectMany(l => new[] { ActionIds.LeafConfigRead(l), ActionIds.LeafConfigWrite(l) })),
        StringComparer.Ordinal);

    /// <summary>The self action every signed-in person holds, whatever their roles: their own devices.</summary>
    private const string SelfAction = ActionIds.PushDevices;

    /// <summary>The actions <paramref name="persona"/> holds, self actions included as the provider lists them.</summary>
    public static IReadOnlySet<string> Of(Persona persona) => new HashSet<string>(
        persona switch
        {
            Persona.Reader => ReaderActions,
            Persona.Runner => RunnerActions,
            Persona.Owner => AllActions,
            _ => new HashSet<string>(),
        },
        StringComparer.Ordinal)
    {
        SelfAction,
    };
}
