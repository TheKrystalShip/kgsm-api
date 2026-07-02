using Microsoft.Extensions.DependencyInjection;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Aggregation;
using TheKrystalShip.Api.Services.Alerts;
using TheKrystalShip.Api.Services.Players;
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
    AlertEngine alerts,
    PlayerRosterService roster,
    PlayerHistoryService history,
    InstanceCache instanceCache,
    ApiOptions options,
    ILogger<KgsmAuditConsumer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Always ensure the audit table exists — even with no engine, GET /audit and auth writes need it.
        await audit.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);

        // Reconcile player roster from the watchdog's live session map (or mark unknown if unavailable).
        await history.ReconcileFromWatchdogAsync(cancellationToken).ConfigureAwait(false);

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
        // server.start / server.restart additionally feed the alert↔audit bridge (M6·a): AFTER the row is
        // written we hand its evt_ id to the AlertEngine (only when IsRecoveryAction), so a crash that
        // clears because a start|restart brought the server back links to that action as
        // resolution.actionId. The hand-off (not a second event-socket binding) is why the consumer owns
        // it. The watchdog's autonomous crash-restart emits instance_restarted (system/system,
        // kgsm-watchdog d4b453f) → a server.restart row through this same handler, so a pure auto-heal
        // bridges too. The watchdog's BOOT-AUTOSTART emits instance_started (system/system) → audited as a
        // server.start row but NOT bridged (a boot bring-up is not a crash recovery — IsRecoveryAction
        // excludes the system-origin start). A stop-cleared crash links null. Honest, never fabricated.
        // See Services/Alerts/CLAUDE.md.
        // Player-presence roster reset (player-presence-contract.md §5): "Reset a server's roster on
        // instance stop/start/restart" — a fresh start invalidates every prior session (new log = new
        // EventChannelTail inode on the watchdog side), a stop obviously ends them all, and a restart is
        // its OWN distinct event (not a stop+start pair) whose underlying process dies without emitting
        // per-player "left" lines — without this reset those sessions would linger as phantom "connected"
        // entries until each one happened to reconnect under a fresh key. Composed INTO these same
        // handlers rather than a second RegisterHandler<...> call — kgsm-lib's EventService keeps one
        // handler per event type (a plain dictionary indexer), so a second consumer registering here
        // would silently replace this audit write, not add to it (see
        // Services/Players/PlayerRosterService.cs remarks).
        events.RegisterHandler<InstanceStartedData>(d =>
        {
            instanceCache.UpdateStatus(d.InstanceName, true);
            roster.Reset(d.InstanceName);
            history.Reset(d.InstanceName);
            return WriteServerAndBridge(d, AuditAction.ServerStart, "started");
        });
        events.RegisterHandler<InstanceStoppedData>(d =>
        {
            instanceCache.UpdateStatus(d.InstanceName, false);
            roster.Reset(d.InstanceName);
            history.Reset(d.InstanceName);
            return WriteServer(d, AuditAction.ServerStop, AuditSeverity.Info, "stopped");
        });
        events.RegisterHandler<InstanceRestartedData>(d =>
        {
            instanceCache.UpdateStatus(d.InstanceName, true);
            roster.Reset(d.InstanceName);
            history.Reset(d.InstanceName);
            return WriteServerAndBridge(d, AuditAction.ServerRestart, "restarted");
        });
        events.RegisterHandler<InstanceUninstalledData>(d =>
        {
            instanceCache.TryRefresh();
            return WriteServer(d, AuditAction.ServerUninstall, AuditSeverity.Warn, "uninstalled");
        });

        // server.update — sourced from the version-changed event (it carries the meaningful old→new
        // detail). A plain instance_updated with no version change produces no row (nothing material
        // changed) — an honest boundary, documented in PLAN §8.
        events.RegisterHandler<InstanceVersionUpdatedData>(d =>
            WriteServer(d, AuditAction.ServerUpdate, AuditSeverity.Info, "updated",
                Meta(("oldVersion", d.OldVersion), ("newVersion", d.NewVersion))));

        // server.install — carries the blueprint it was installed from.
        events.RegisterHandler<InstanceInstalledData>(d =>
        {
            instanceCache.TryRefresh();
            return WriteServer(d, AuditAction.ServerInstall, AuditSeverity.Success, "installed",
                Meta(("blueprint", d.Blueprint)));
        });

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
        {
            instanceCache.UpdateStatus(d.InstanceName, false);
            return audit.AppendAsync(AuditMapping.FromCrashEvent(d, options.HostId));
        });
        events.RegisterHandler<InstanceFailedData>(d =>
        {
            instanceCache.UpdateStatus(d.InstanceName, false);
            return audit.AppendAsync(AuditMapping.FromFailedEvent(d, options.HostId));
        });

        // network.ports.open / .close — the CLI-path firewall echoes (kgsm bash emits these on a
        // confirmed open/close via create, firewall-enable/disable, or uninstall — kgsm-lib 1.12.0).
        // Both recorded so the trail is symmetric. The api-issued `open_ports` command writes
        // `network.ports.open` directly at M6·b (kgsm runs nothing → no echo, the auth.* case); there is
        // no api close command (§3·g is open-only), so `network.ports.close` is cleanly CLI-echo-only.
        events.RegisterHandler<InstancePortsOpenedData>(d =>
            audit.AppendAsync(AuditMapping.FromPortsOpenedEvent(d, options.HostId)));
        events.RegisterHandler<InstancePortsClosedData>(d =>
            audit.AppendAsync(AuditMapping.FromPortsClosedEvent(d, options.HostId)));

        // network.upnp.open / .close — the watchdog's ROUTER-forward echoes (kgsm-lib 1.21.0). Distinct
        // from the firewall ports.* above: the watchdog opens/closes UPnP mappings on the IGD via upnpc
        // on bring-up/stop and emits these (system/system) only on a confirmed upnpc-exit-0 transition.
        // Watchdog-echo-only (no api-issued UPnP command). Pure mapper, socket-free. Engine-owned → no double-write.
        events.RegisterHandler<InstanceUpnpOpenedData>(d =>
            audit.AppendAsync(AuditMapping.FromUpnpOpenedEvent(d, options.HostId)));
        events.RegisterHandler<InstanceUpnpClosedData>(d =>
            audit.AppendAsync(AuditMapping.FromUpnpClosedEvent(d, options.HostId)));

        // player.join / player.leave — presence echoes (kgsm-lib 1.19.0, extended 1.29.0 with
        // addr/sessionKey/reason). For our container images the watchdog forwards these from the in-image
        // detection shim; native log-scraping detection emits the identical shape. The player id/name/addr
        // (nullable, at-least-one guaranteed by the emitting side) plus sessionKey/reason ride in the audit
        // meta (AuditMapping.FromPlayer*Event, pure/socket-free). Engine-owned → no double-write. ALSO
        // drives the live roster projection (PlayerRosterService) — composed here rather than a second
        // RegisterHandler<InstancePlayerJoinedData/-LeftData> call for the same single-handler-per-type
        // reason as the start/stop reset above.
        events.RegisterHandler<InstancePlayerJoinedData>(d =>
        {
            roster.Join(d.InstanceName, d.SessionKey, d.PlayerId, d.PlayerName, d.PlayerAddr,
                d.Timestamp ?? DateTimeOffset.UtcNow);
            history.Join(d.InstanceName, d.SessionKey, d.PlayerId, d.PlayerName, d.PlayerAddr,
                d.Timestamp ?? DateTimeOffset.UtcNow);
            return audit.AppendAsync(AuditMapping.FromPlayerJoinedEvent(d, options.HostId));
        });
        events.RegisterHandler<InstancePlayerLeftData>(d =>
        {
            roster.Leave(d.InstanceName, d.SessionKey, d.PlayerId, d.PlayerName, d.PlayerAddr,
                d.Timestamp ?? DateTimeOffset.UtcNow);
            history.Leave(d.InstanceName, d.SessionKey, d.PlayerId, d.PlayerName, d.PlayerAddr,
                d.Timestamp ?? DateTimeOffset.UtcNow);
            return audit.AppendAsync(AuditMapping.FromPlayerLeftEvent(d, options.HostId));
        });

        // config.set — instance config edits (kgsm-lib 1.22.0). The PATCH /servers/{id}/config path stamps
        // actor+origin onto SetInstanceConfigValue, so this echo carries provenance; engine-owned → no
        // double-write (plain AppendAsync, NOT WriteServerAndBridge — a config edit is not a recovery action).
        // KEY ONLY in meta (the event never carries the value — secret hygiene). Pure mapper, socket-free.
        events.RegisterHandler<InstanceConfigChangedData>(d =>
            audit.AppendAsync(AuditMapping.FromConfigChangedEvent(d, options.HostId)));

        // console.input — an arbitrary console command sent to a running native instance (kgsm-lib 1.24.0).
        // The POST /servers/{id}/console path stamps actor+origin onto SendInput, so this echo carries
        // provenance; engine-owned → no double-write (plain AppendAsync — not a recovery action). Unlike
        // config.set, the FULL command text rides in meta (recording what was run — see AuditAction.ConsoleInput).
        events.RegisterHandler<InstanceInputSentData>(d =>
            audit.AppendAsync(AuditMapping.FromInputSentEvent(d, options.HostId)));
    }

    private Task WriteServer(
        EventDataBase data, string action, string severity, string verb,
        IReadOnlyDictionary<string, string>? meta = null) =>
        audit.AppendAsync(AuditMapping.FromServerEvent(data, action, severity, verb, options.HostId, meta));

    // Write a server.start/server.restart row, then — only if it is a RECOVERY action — hand its id to
    // the alert engine: a crash that clears because THIS recovery brought the server back links to it
    // (resolution.actionId — M6·a). A stop is not a recovery (it never reaches here, separate handler).
    private async Task WriteServerAndBridge(EventDataBase data, string action, string verb)
    {
        AuditRecord row = await audit
            .AppendAsync(AuditMapping.FromServerEvent(data, action, AuditSeverity.Info, verb, options.HostId))
            .ConfigureAwait(false);
        if (IsRecoveryAction(data, action) && !string.IsNullOrEmpty(data.InstanceName))
            alerts.NoteRecoveryAction(data.InstanceName, row.Id, row.Ts);
    }

    // Whether a start/restart row is a RECOVERY action eligible to become a resolved crash's
    // resolution.actionId (the alert↔audit bridge). A human start (operator/api/discord) and the
    // watchdog's autonomous crash-RESTART recover a crashed server, so they bridge. A watchdog
    // BOOT-AUTOSTART — the sole source of a system-origin server.start (kgsm-watchdog RespawnFresh; a
    // caller may never declare origin=system, AuditOrigin.IsCallerDeclarable) — is a fresh bring-up at
    // boot, not a response to a crash; bridging it could stamp a stale id on a later crash whose own
    // recovery event happened to drop (the emit is best-effort), so it is audited but NEVER bridged.
    // Keyed on ORIGIN, not "is it the watchdog", so any future autonomous start path inherits the safe
    // non-bridging default rather than silently linking a stale id.
    //   NOTE: the broad root-cause is now CLOSED — AlertEngine episode-scopes the bridge by timestamp (a
    //   stashed action stamps a resolution only if it post-dates that crash's raise), so a dropped recovery
    //   event for ANY start (operator OR system) can no longer leave a stale id to mislink a later crash.
    //   This origin exclusion is therefore now defense-in-depth/semantic (a boot bring-up simply isn't a
    //   recovery) rather than the sole guard — kept so the intent is explicit and any future autonomous
    //   start stays non-bridging by default; episode-scoping alone would also reject a boot start's
    //   pre-crash timestamp. See Services/Alerts/AlertEngine.BuildResolution.
    internal static bool IsRecoveryAction(EventDataBase data, string action) =>
        action != AuditAction.ServerStart || !IsSystemOrigin(data);

    private static bool IsSystemOrigin(EventDataBase data) =>
        string.Equals(data.Origin, AuditOrigin.System, StringComparison.OrdinalIgnoreCase);

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
