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
[assembly: Action(ActionIds.PushDevices, "Read your own notifications and register your push devices", DeclaredEffect.Write, DeclaredScope.Node, Self = true)]
