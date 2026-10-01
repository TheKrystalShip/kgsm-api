using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.KGSM.ComponentConfig;

// The actions this API performs itself, which TheKrystalShip.KGSM.ComponentConfig writes into
// deploy/kgsm-api.leaf.actions.json. Each is checked where its surface is served (ActionIds names the
// gates); the engine's and every leaf's actions are declared in their own manifests.

[assembly: Action(ActionIds.HostsRead, "View this host", DeclaredEffect.Read, DeclaredScope.Node)]
[assembly: Action(ActionIds.HostsWrite, "Change this host's label and region", DeclaredEffect.Write, DeclaredScope.Node)]
[assembly: Action(ActionIds.BatchesRead, "View batches", DeclaredEffect.Read, DeclaredScope.Node)]
[assembly: Action(ActionIds.BatchesCancel, "Cancel batches", DeclaredEffect.Execute, DeclaredScope.Node)]
[assembly: Action(ActionIds.AlertsRead, "View alerts", DeclaredEffect.Read, DeclaredScope.Node)]
[assembly: Action(ActionIds.AuditRead, "View the audit log", DeclaredEffect.Read, DeclaredScope.Node)]
[assembly: Action(ActionIds.AuditPersonalFields, "See personal details in the audit log", DeclaredEffect.Read, DeclaredScope.Node)]
[assembly: Action(ActionIds.DiagnosticsRead, "Run diagnostics", DeclaredEffect.Read, DeclaredScope.Node)]
[assembly: Action(ActionIds.LogsRead, "Read this host's service logs", DeclaredEffect.Read, DeclaredScope.Node)]
[assembly: Action(ActionIds.IntegrationsManage, "Manage notification integrations", DeclaredEffect.Write, DeclaredScope.Cluster)]
[assembly: Action(ActionIds.ServicesRead, "View this host's services", DeclaredEffect.Read, DeclaredScope.Node)]
[assembly: Action(ActionIds.ServicesManage, "Connect, disconnect and restart services", DeclaredEffect.Execute, DeclaredScope.Node)]
[assembly: Action(ActionIds.MembersRead, "View the cluster's members", DeclaredEffect.Read, DeclaredScope.Cluster)]
[assembly: Action(ActionIds.MembersManage, "Manage the cluster's members", DeclaredEffect.Write, DeclaredScope.Cluster)]
[assembly: Action(ActionIds.MembersRemove, "Remove members from the cluster", DeclaredEffect.Write, DeclaredScope.Cluster)]
[assembly: Action(ActionIds.PushDevices, "Register your own push devices", DeclaredEffect.Write, DeclaredScope.Node, Self = true)]

// What this API reads from the engine as itself, for everybody: the roster, run state and players behind
// every page and the realtime stream, the library catalog, the engine's configuration, each console the
// stream follows, and the backups the server cards count. What reaches a person is cut to what that
// person may read, where it is served.
[assembly: Requires(ActionIds.ServerRead, DeclaredScope.Instance,
    "Keeps the server roster, run state and players current for the panel and the realtime stream.")]
[assembly: Requires(ActionIds.LibraryRead, DeclaredScope.Node,
    "Keeps the installable-game catalog current.")]
[assembly: Requires(ActionIds.EngineConfigRead, DeclaredScope.Node,
    "Reads where the engine keeps its libraries and how much memory it may commit.")]
[assembly: Requires(ActionIds.ServerConsoleRead, DeclaredScope.Instance,
    "Follows each console the realtime stream carries.")]
[assembly: Requires(ActionIds.ServerBackupsRead, DeclaredScope.Instance,
    "Counts each server's backups for its card.")]
