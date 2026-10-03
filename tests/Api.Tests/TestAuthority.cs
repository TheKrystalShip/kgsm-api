using System.Collections.Concurrent;
using System.Reflection;

using Microsoft.Extensions.DependencyInjection;

using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Access;
using TheKrystalShip.KGSM.Auth.Users;

using ActionIds = TheKrystalShip.Api.Services.Auth.ActionIds;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Who a test's caller is on the node: nobody's grant at all, a reader, somebody running servers, or an
/// Owner. Each is a real role in the replica, holding the actions in <see cref="TestAuthority"/>.
/// </summary>
public enum Persona
{
    /// <summary>An account holding no role.</summary>
    None,

    /// <summary>The reads the panel's pages sit on.</summary>
    Reader,

    /// <summary>The reads and everything that runs a server: lifecycle, console, files, backups, players.</summary>
    Runner,

    /// <summary>Every action, declared or not.</summary>
    Owner,
}

/// <summary>
/// The test's stand-in for the auth anchor: an authority store of its own beside the node's replica, the
/// personas as roles in it, and the snapshot the anchor would send delivered into the replica after every
/// change — so the node reads exactly what replication would have left it.
/// </summary>
internal sealed class TestAuthority
{
    private static readonly ConcurrentDictionary<string, TestAuthority> ByReplica = new(StringComparer.Ordinal);

    // Long enough that no test outlives it: the replica stays current for the whole run.
    private static readonly TimeSpan Bound = TimeSpan.FromDays(1);

    private const string RootName = "root";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SqliteAuthorityStore _anchor;
    private readonly SqliteAuthorityStore _replica;
    private string? _root;
    private string? _readers;
    private string? _runners;

    private TestAuthority(string replicaPath)
    {
        _anchor = new SqliteAuthorityStore(new UserStoreOptions { Path = replicaPath + ".anchor.db" });
        _replica = new SqliteAuthorityStore(new UserStoreOptions { Path = replicaPath });
    }

    /// <summary>The stand-in anchor for the node behind <paramref name="services"/>.</summary>
    public static TestAuthority For(IServiceProvider services) =>
        ByReplica.GetOrAdd(services.GetRequiredService<ApiOptions>().UsersDbPath, path => new TestAuthority(path));

    /// <summary>A stand-in anchor and replica of their own at <paramref name="replicaPath"/>, for a test with no node.</summary>
    public static TestAuthority At(string replicaPath) => new(replicaPath);

    /// <summary>The replica the node reads, opened directly.</summary>
    public SqliteAuthorityStore Replica => _replica;

    /// <summary>An evaluator over the replica as it stands — what the node's own reads produce.</summary>
    public AccessEvaluator Evaluator() =>
        new(new AuthoritySource(_replica, AuthorityStanding.Replica).CurrentAsync().GetAwaiter().GetResult());

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
        ActionIds.SpeechStatusRead, ActionIds.BotStatusRead, ActionIds.ReactorRulesRead,
    };

    /// <summary>
    /// Give <paramref name="identity"/> an account holding <paramref name="persona"/>, at
    /// <paramref name="status"/>, and deliver it to the node.
    /// </summary>
    public KgsmUser Set(KgsmIdentity identity, Persona persona, UserStatus status = UserStatus.Active) =>
        Locked(async () =>
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            string root = await RootAsync(now);

            KgsmUser account = await _anchor.FindByCredentialAsync(identity.Handle)
                ?? (await new IdentityLinkService(_anchor).ProvisionAsync(identity, AccountOrigin.Admitted, status, now)).User!;
            if (account.Status != status)
            {
                account = account with { Status = status, Updated = now };
                await _anchor.UpdateAsync(account);
            }

            AuthoritySnapshot held = await _anchor.LoadAsync();
            foreach (Assignment assignment in held.AssignmentsOf(account.UserId))
                await EditAsync(root, new Revoke(assignment.AssignmentId), now);

            switch (persona)
            {
                case Persona.Owner:
                    await _anchor.GrantOwnerLocallyAsync(account.Username, "local:test", now);
                    break;
                case Persona.Reader:
                    await EditAsync(root, new Assign(account.UserId, _readers!, AccessScope.Cluster), now);
                    break;
                case Persona.Runner:
                    await EditAsync(root, new Assign(account.UserId, _runners!, AccessScope.Cluster), now);
                    break;
            }

            await DeliverAsync(now);
            return account;
        });

    /// <summary>
    /// Grant <paramref name="accountId"/> one action at <paramref name="scope"/> alone — a role of its own
    /// holding it — and deliver it.
    /// </summary>
    public void Grant(string accountId, string action, AccessScope scope) =>
        Locked(async () =>
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            string root = await RootAsync(now);
            string name = "grant-" + Guid.NewGuid().ToString("N")[..8];
            string role = await RoleAsync(name, new HashSet<string>(StringComparer.Ordinal) { action }, now);
            await EditAsync(root, new Assign(accountId, role, scope), now);
            await DeliverAsync(now);
            return true;
        });

    /// <summary>Remove the account <paramref name="identity"/> proves, and deliver it.</summary>
    public void Remove(KgsmIdentity identity) =>
        Locked(async () =>
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            await RootAsync(now);
            if (await _anchor.FindByCredentialAsync(identity.Handle) is { } account)
                await _anchor.DeleteAsync(account.UserId);

            await DeliverAsync(now);
            return true;
        });

    /// <summary>The account <paramref name="identity"/> proves on the node, or <see langword="null"/>.</summary>
    public KgsmUser? AccountOf(KgsmIdentity identity) =>
        _replica.FindByCredentialAsync(identity.Handle).GetAwaiter().GetResult();

    private T Locked<T>(Func<Task<T>> work)
    {
        _gate.Wait();
        try
        {
            return work().GetAwaiter().GetResult();
        }
        finally
        {
            _gate.Release();
        }
    }

    // The catalog, an Owner to make every edit as, and the two roles — once per stand-in anchor.
    private async Task<string> RootAsync(DateTimeOffset now)
    {
        if (_root is not null)
            return _root;

        await _anchor.ReplaceCatalogAsync(Catalog(), now);

        var root = new KgsmIdentity("local", "root", RootName, RootName, null, []);
        KgsmUser account = (await new IdentityLinkService(_anchor)
            .ProvisionAsync(root, AccountOrigin.Admitted, UserStatus.Active, now)).User!;
        await _anchor.GrantOwnerLocallyAsync(account.Username, "local:test", now);
        _root = account.UserId;

        _readers = await RoleAsync("Readers", ReaderActions, now);
        _runners = await RoleAsync("Runners", RunnerActions, now);
        return _root;
    }

    private async Task<string> RoleAsync(string name, IReadOnlySet<string> actions, DateTimeOffset now)
    {
        string permission = (await EditAsync(_root!, new CreatePermission(name), now)).CreatedId!;
        await EditAsync(_root!, new SetPermissionActions(permission, actions.ToHashSet(StringComparer.Ordinal)), now);
        string role = (await EditAsync(_root!, new CreateRole(name), now)).CreatedId!;
        await EditAsync(_root!, new SetRolePermissions(role, new HashSet<string> { permission }), now);
        return role;
    }

    private async Task<AuthorityWrite> EditAsync(string actor, AuthorityEdit edit, DateTimeOffset now) =>
        await _anchor.ApplyAsync(actor, edit, await _anchor.VersionAsync(), now);

    private async Task DeliverAsync(DateTimeOffset now) =>
        await _replica.ApplySnapshotAsync(await _anchor.ExportAsync(Bound, now), now);

    /// <summary>
    /// Every action the API gates on, as the components performing them declare: a server's at the
    /// instance, the cluster's at the cluster, the rest at the node.
    /// </summary>
    private static IReadOnlyCollection<CatalogAction> Catalog() =>
    [
        .. typeof(ActionIds).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f is { IsLiteral: true } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .Select(id => new CatalogAction(id, id, EffectOf(id), ScopeOf(id), Self: id == ActionIds.PushDevices)),
    ];

    private static ActionEffect EffectOf(string id) =>
        id.EndsWith(".read", StringComparison.Ordinal) ? ActionEffect.Read : ActionEffect.Write;

    private static ScopeKind ScopeOf(string id) => id switch
    {
        ActionIds.ServerInstall => ScopeKind.Node,
        ActionIds.LibraryRead or ActionIds.IntegrationsManage
            or ActionIds.MembersRead or ActionIds.MembersManage or ActionIds.MembersRemove => ScopeKind.Cluster,
        _ when id.StartsWith("kgsm:server.", StringComparison.Ordinal) => ScopeKind.Instance,
        _ => ScopeKind.Node,
    };
}
