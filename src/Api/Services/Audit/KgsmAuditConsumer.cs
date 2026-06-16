using Microsoft.Extensions.DependencyInjection;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.Api.Services.Audit;

/// <summary>
/// Subscribes to the kgsm event stream (via kgsm-lib's <see cref="IEventService"/> — the C#↔engine
/// chokepoint, never a raw socket) and turns each lifecycle event into one append-only audit row (M5).
/// This is the <b>single writer for <c>server.*</c>/<c>backup.*</c></b>: kgsm owns those actions, so the
/// API records the engine's <em>echo</em> rather than double-writing when it issues a command — the
/// resulting event already carries the provenance the command path stamped (<c>Actor</c>/<c>Origin</c>).
/// Watchdog-driven (autonomous <c>system</c>) and direct-CLI actions flow through the very same path.
/// </summary>
/// <remarks>
/// <para><b>Listener lifetime &amp; the honest boundary.</b> The engine is stateless and does not
/// backfill — events emitted while this consumer is not listening are <em>never</em> audited. That is
/// inherent to a downstream-consumer design (CLAUDE.md invariant #5), not a bug. kgsm only delivers to a
/// socket file that already exists, so the API must be up and bound first.</para>
/// <para><b>Degrades gracefully.</b> If the engine is unprovisioned, or <see cref="IEventService"/> is
/// absent, or binding the event socket fails, the consumer logs and does nothing further — <c>GET
/// /audit</c> and the API-internal (auth) writes still work; only the engine-sourced rows are missing.
/// Startup never fails on the event socket.</para>
/// </remarks>
public sealed class KgsmAuditConsumer(
    IServiceProvider services,
    AuditService audit,
    ApiOptions options,
    ILogger<KgsmAuditConsumer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Always ensure the audit table exists — even with no engine, GET /audit and auth writes need it.
        await audit.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);

        if (!options.KgsmProvisioned)
        {
            logger.LogInformation("Audit: kgsm engine not provisioned — engine-sourced audit is off "
                + "(GET /audit + API-internal audit still active).");
            return;
        }

        IEventService? events = services.GetService<IEventService>();
        if (events is null)
        {
            logger.LogWarning("Audit: IEventService unavailable — engine-sourced audit is off.");
            return;
        }

        RegisterHandlers(events);

        try
        {
            // Binds the kgsm event socket and starts the background listener. A bind failure faults the
            // listener's own fire-and-forget task (logged by kgsm-lib) without throwing here — so a bad
            // socket path degrades to "no engine events" rather than crashing the API.
            events.Initialize();
            logger.LogInformation("Audit: listening for kgsm events on {Socket}", options.KgsmSocketPath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Audit: failed to initialize the kgsm event listener — "
                + "engine-sourced audit is off.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void RegisterHandlers(IEventService events)
    {
        // server.* — the closed lifecycle subset kgsm emits today. Each maps 1:1 to a dotted action.
        events.RegisterHandler<InstanceStartedData>(d =>
            WriteServer(d, AuditAction.ServerStart, AuditSeverity.Info, "started"));
        events.RegisterHandler<InstanceStoppedData>(d =>
            WriteServer(d, AuditAction.ServerStop, AuditSeverity.Info, "stopped"));
        events.RegisterHandler<InstanceRestartedData>(d =>
            WriteServer(d, AuditAction.ServerRestart, AuditSeverity.Info, "restarted"));
        events.RegisterHandler<InstanceUninstalledData>(d =>
            WriteServer(d, AuditAction.ServerUninstall, AuditSeverity.Warn, "uninstalled"));

        // server.update — sourced from the version-changed event (it carries the meaningful old→new
        // detail). A plain instance_updated with no version change produces no row (nothing material
        // changed) — an honest boundary, documented in PLAN §8.
        events.RegisterHandler<InstanceVersionUpdatedData>(d =>
            WriteServer(d, AuditAction.ServerUpdate, AuditSeverity.Info, "updated",
                Meta(("oldVersion", d.OldVersion), ("newVersion", d.NewVersion))));

        // server.install — carries the blueprint it was installed from.
        events.RegisterHandler<InstanceInstalledData>(d =>
            WriteServer(d, AuditAction.ServerInstall, AuditSeverity.Success, "installed",
                Meta(("blueprint", d.Blueprint))));

        // backup.* — source + version of the snapshot.
        events.RegisterHandler<InstanceBackupCreatedData>(d =>
            WriteServer(d, AuditAction.BackupCreate, AuditSeverity.Success, "backed up",
                Meta(("source", d.Source), ("version", d.Version))));
        events.RegisterHandler<InstanceBackupRestoredData>(d =>
            WriteServer(d, AuditAction.BackupRestore, AuditSeverity.Success, "restored backup for",
                Meta(("source", d.Source), ("version", d.Version))));

        // server.crash — the resident supervisor's autonomous signals (kgsm-watchdog, kgsm-lib 1.9.0),
        // both stamped Actor/Origin = "system" upstream. Per-event policy (action/severity/summary/meta)
        // lives in the pure AuditMapping mappers so it is unit-tested without a live socket (M6·0).
        events.RegisterHandler<InstanceCrashedData>(d =>
            audit.AppendAsync(AuditMapping.FromCrashEvent(d, options.HostId)));
        events.RegisterHandler<InstanceFailedData>(d =>
            audit.AppendAsync(AuditMapping.FromFailedEvent(d, options.HostId)));

        // network.ports.open / .close — the CLI-path firewall echoes (kgsm bash emits these on a
        // confirmed open/close via create, firewall-enable/disable, or uninstall — kgsm-lib 1.12.0).
        // Both recorded so the trail is symmetric. The api-issued `open_ports` command writes
        // `network.ports.open` directly at M6·b (kgsm runs nothing → no echo, the auth.* case); there is
        // no api close command (§3·g is open-only), so `network.ports.close` is cleanly CLI-echo-only.
        events.RegisterHandler<InstancePortsOpenedData>(d =>
            audit.AppendAsync(AuditMapping.FromPortsOpenedEvent(d, options.HostId)));
        events.RegisterHandler<InstancePortsClosedData>(d =>
            audit.AppendAsync(AuditMapping.FromPortsClosedEvent(d, options.HostId)));
    }

    private Task WriteServer(
        EventDataBase data, string action, string severity, string verb,
        IReadOnlyDictionary<string, string>? meta = null) =>
        audit.AppendAsync(AuditMapping.FromServerEvent(data, action, severity, verb, options.HostId, meta));

    // Build a meta dict from non-empty pairs (a blank value is omitted, never stored as ""). Null if empty.
    private static IReadOnlyDictionary<string, string>? Meta(params (string Key, string? Value)[] pairs)
    {
        Dictionary<string, string>? meta = null;
        foreach ((string key, string? value) in pairs)
        {
            if (string.IsNullOrEmpty(value)) continue;
            meta ??= new Dictionary<string, string>(pairs.Length);
            meta[key] = value;
        }
        return meta;
    }
}
