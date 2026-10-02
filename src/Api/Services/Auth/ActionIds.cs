namespace TheKrystalShip.Api.Services.Auth;

/// <summary>
/// Every action this API gates on, by the id the component performing it declares.
/// </summary>
/// <remarks>
/// <para>
/// <c>api:*</c> are this API's own and declared on its assembly (<c>ApiActionDeclarations.cs</c>); the
/// rest belong to the engine and to each leaf, and are declared in their manifests. An action no installed
/// manifest declares is an Owner's alone.
/// </para>
/// <para>
/// The ids are the ones the panel names in <c>kgsm-web/src/lib/actions.js</c>. A rename happens in both
/// places or in neither.
/// </para>
/// </remarks>
public static class ActionIds
{
    // --- the engine ---
    public const string LibraryRead = "kgsm:library.read";
    public const string BlueprintsWrite = "kgsm:blueprints.write";
    public const string LibrariesManage = "kgsm:libraries.manage";
    public const string EngineConfigRead = "kgsm:engine.config.read";
    public const string EngineConfigWrite = "kgsm:engine.config.write";
    public const string ServerRead = "kgsm:server.read";
    public const string ServerInstall = "kgsm:server.install";
    public const string ServerUninstall = "kgsm:server.uninstall";
    public const string ServerMove = "kgsm:server.move";
    public const string ServerStart = "kgsm:server.start";
    public const string ServerStop = "kgsm:server.stop";
    public const string ServerRestart = "kgsm:server.restart";
    public const string ServerUpdate = "kgsm:server.update";
    public const string ServerAnnounce = "kgsm:server.announce";
    public const string ServerConfigRead = "kgsm:server.config.read";
    public const string ServerConfigWrite = "kgsm:server.config.write";
    public const string ServerWindowsWrite = "kgsm:server.windows.write";
    public const string ServerConsoleRead = "kgsm:server.console.read";
    public const string ServerConsoleWrite = "kgsm:server.console.write";
    public const string ServerFilesRead = "kgsm:server.files.read";
    public const string ServerFilesWrite = "kgsm:server.files.write";
    public const string ServerBackupsRead = "kgsm:server.backups.read";
    public const string ServerBackupsCreate = "kgsm:server.backups.create";
    public const string ServerBackupsRestore = "kgsm:server.backups.restore";
    public const string ServerBackupsManage = "kgsm:server.backups.manage";
    public const string ServerPlayersKick = "kgsm:server.players.kick";
    public const string ServerPlayersBan = "kgsm:server.players.ban";

    // --- this API ---
    public const string HostsRead = "api:hosts.read";
    public const string HostsWrite = "api:hosts.write";
    public const string BatchesRead = "api:batches.read";
    public const string BatchesCancel = "api:batches.cancel";
    public const string AlertsRead = "api:alerts.read";
    public const string AuditRead = "api:audit.read";
    public const string AuditPersonalFields = "api:audit.personal-fields";
    public const string DiagnosticsRead = "api:diagnostics.read";
    public const string LogsRead = "api:logs.read";
    public const string IntegrationsManage = "api:integrations.manage";
    public const string ServicesRead = "api:services.read";
    public const string ServicesManage = "api:services.manage";
    public const string MembersRead = "api:members.read";
    public const string MembersManage = "api:members.manage";
    public const string MembersRemove = "api:members.remove";
    public const string PushDevices = "api:push.devices";

    // --- the leaves this API relays to ---
    public const string SchedulerWindowsRead = "scheduler:windows.read";
    public const string SchedulerWindowsWrite = "scheduler:windows.write";
    public const string WatchdogSupervisionRead = "watchdog:supervision.read";
    public const string MonitorMetricsRead = "monitor:metrics.read";
    public const string MonitorThresholdsRead = "monitor:thresholds.read";
    public const string MonitorThresholdsWrite = "monitor:thresholds.write";
    public const string FirewallRulesRead = "firewall:rules.read";
    public const string SpeechStatusRead = "speech:status.read";
    public const string BotStatusRead = "bot:status.read";
    public const string ReactorRulesRead = "reactor:rules.read";
    public const string ReactorRulesWrite = "reactor:rules.write";

    /// <summary>Every lifecycle verb <see cref="ForVerb"/> answers for — what the operations publish.</summary>
    public static readonly IReadOnlyList<string> Verbs =
        [Contracts.CommandVerb.Start, Contracts.CommandVerb.Stop, Contracts.CommandVerb.Restart, Contracts.CommandVerb.Update];

    /// <summary>The action a lifecycle verb performs, or <see langword="null"/> for a verb outside the set.</summary>
    public static string? ForVerb(string? verb) => verb switch
    {
        Contracts.CommandVerb.Start => ServerStart,
        Contracts.CommandVerb.Stop => ServerStop,
        Contracts.CommandVerb.Restart => ServerRestart,
        Contracts.CommandVerb.Update => ServerUpdate,
        _ => null,
    };

    /// <summary>A leaf's standard configuration surface: <c>&lt;leaf&gt;:config.read</c>.</summary>
    public static string LeafConfigRead(string leaf) => leaf + ":config.read";

    /// <summary>A leaf's standard configuration surface: <c>&lt;leaf&gt;:config.write</c>.</summary>
    public static string LeafConfigWrite(string leaf) => leaf + ":config.write";

    /// <summary>The engine's id among the leaves, whose configuration is the engine's own action.</summary>
    public const string EngineLeaf = "kgsm";

    /// <summary>
    /// The action reading or writing <paramref name="leaf"/>'s configuration performs — the leaf's own,
    /// and the engine's for the engine.
    /// </summary>
    public static string ConfigAction(string leaf, bool write) =>
        string.Equals(leaf, EngineLeaf, StringComparison.Ordinal)
            ? (write ? EngineConfigWrite : EngineConfigRead)
            : (write ? LeafConfigWrite(leaf) : LeafConfigRead(leaf));
}
