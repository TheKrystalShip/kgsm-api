using Microsoft.Extensions.DependencyInjection;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Realtime;
using TheKrystalShip.Api.Services.Aggregation;
using TheKrystalShip.Api.Services.Alerts;
using TheKrystalShip.Api.Services.Commands;
using TheKrystalShip.Api.Services.Players;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.Api.Services.Audit;

/// <summary>
/// Subscribes to the kgsm event stream (via kgsm-lib's <see cref="IEventService"/> — the C#↔engine
/// chokepoint, never a raw socket) and turns each lifecycle event into a live <c>audit</c> WS push +
/// (for start/restart) the alert↔audit recovery bridge. As of event-history-plan.md Phase C this
/// consumer no longer <b>persists</b> engine-sourced rows: kgsm-monitor is the single source of truth
/// for engine history (it persists the raw envelope neutrally; <c>GET /audit</c> merges it in at read
/// time, shaped via <see cref="AuditMapping"/>/<see cref="MonitorEventShaping"/>). This consumer stays
/// the live-consumption path — realtime SSE and outbound notifications fire exactly as before
/// (<see cref="AuditService.PublishLive"/>) — it simply no longer writes those rows to the local table.
/// Watchdog-driven (autonomous <c>system</c>) and direct-CLI actions flow through the very same path.
/// </summary>
/// <remarks>
/// <para><b>Live-only, not a backfill.</b> A client subscribed to the <c>audit</c> WS topic sees an
/// engine event the instant it arrives; a client paging <c>GET /audit</c> sees it once kgsm-monitor has
/// persisted and served it back through the merge. Nothing here writes to <c>AppDbContext.Audit</c> for
/// an engine-sourced action any more — API-only actions (auth/session/leaf/files/console-audit) still
/// do, via a direct <see cref="AuditService.AppendAsync"/> elsewhere (unaffected by this change).</para>
/// <para><b>Degrades gracefully.</b> If the engine is unprovisioned, or <see cref="IEventService"/> is
/// absent, or binding the event socket fails, the consumer logs and does nothing further — <c>GET
/// /audit</c> and the API-internal (auth) writes still work; only the live engine push is missing.
/// Startup never fails on the event socket.</para>
/// </remarks>
public sealed class KgsmAuditConsumer(
    IServiceProvider services,
    AuditService audit,
    AlertEngine alerts,
    PlayerRosterService roster,
    PlayerHistoryService history,
    InstanceCache instanceCache,
    Services.Library.BlueprintCache blueprintCache,
    Aggregation.UpdateCheckCache updateCheckCache,
    Aggregation.BackupCache backupCache,
    ApiOptions options,
    StreamHub hub,
    JobRegistry jobRegistry,
    ILogger<KgsmAuditConsumer> logger) : IHostedService
{
    // Captures the deterministic AuditId.ForEvent id for each engine envelope (via a RegisterRawHandler
    // hook — see EngineEventIdTracker remarks) so the typed handlers below, which only ever receive the
    // typed EventDataBase, can still tag their published-but-not-persisted rows with the SAME id
    // kgsm-monitor independently computed for the identical event.
    private readonly EngineEventIdTracker idTracker = new();

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
        // Captures AuditId.ForEvent for the envelope about to be typed-dispatched — see
        // EngineEventIdTracker + TakePendingEventId. Registered first so it is armed before any typed
        // handler below can run (RegisterRawHandler fires before typed dispatch for every event anyway,
        // but registration order here has no bearing on that — kgsm-lib always runs ALL raw handlers
        // before typed dispatch, regardless of relative registration order).
        events.RegisterRawHandler(idTracker.OnRawEvent);

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
            // MarkStarting (not UpdateStatus) — the process has only just spawned, not finished booting.
            // It still flips the boolean run-state to "up" (same as before), but ALSO opens the
            // InstanceCache starting latch, so ServerAggregator.BuildServer reports `starting`, not
            // `running`, until the matching instance_ready arrives below (or a stop/crash closes it).
            instanceCache.MarkStarting(d.InstanceName);
            roster.Reset(d.InstanceName);
            history.Reset(d.InstanceName);
            return WriteServerAndBridge(d, AuditAction.ServerStart, "started");
        });
        // instance_ready (kgsm-lib 1.35.0) — the watchdog's readiness signal: its log-scrape confirms the
        // game finished booting, distinct from instance_started (the process merely spawned). AUDIT-SILENT
        // by design, like the install-phase progress handlers below: server.start already recorded the
        // meaningful fact ("this server was started"); "it finished booting" is a run-state refinement of
        // that SAME action, not a new fact worth its own append-only row, and there is no honest slot in
        // the closed AuditAction vocabulary for it that wouldn't just duplicate server.start. Only closes
        // the starting latch (InstanceCache.MarkReady) so status flips starting -> running; DomainPump's
        // existing Status diff (Realtime/CLAUDE.md) fans that out over SSE with no pump change needed.
        events.RegisterHandler<InstanceReadyData>(d =>
        {
            instanceCache.MarkReady(d.InstanceName);
            return Task.CompletedTask;
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
        // The handler also immediately voids the stale "update available" reading for this instance
        // (via UpdateCheckCache.MarkUpdated) so the next server.patch carries the cleared state —
        // this is the authoritative fallback for CLI-driven updates; the API-command path does the
        // same synchronously in CommandRunner.RunUpdate (the fast-path guarantee).
        events.RegisterHandler<InstanceVersionUpdatedData>(d =>
        {
            updateCheckCache.MarkUpdated(d.InstanceName);
            return WriteServer(d, AuditAction.ServerUpdate, AuditSeverity.Info, "updated",
                Meta(("oldVersion", d.OldVersion), ("newVersion", d.NewVersion)));
        });

        // server.install — carries the blueprint it was installed from.
        events.RegisterHandler<InstanceInstalledData>(d =>
        {
            instanceCache.TryRefresh();
            return WriteServer(d, AuditAction.ServerInstall, AuditSeverity.Success, "installed",
                Meta(("blueprint", d.Blueprint)));
        });

        // backup.* — source + version of the snapshot. Both also re-scan that ONE instance's backups, so
        // server.lastBackup/backupCount reflect the change within a tick instead of waiting out the scan
        // cadence. This is what makes an operator see their own backup land, and it covers a backup taken
        // straight from the CLI just as well, since the trigger is the engine's event rather than our own
        // command path. Fire-and-forget: the scan must never delay or fail the audit push.
        events.RegisterHandler<InstanceBackupCreatedData>(d =>
        {
            RefreshBackupsOf(d.InstanceName);
            return WriteServer(d, AuditAction.BackupCreate, AuditSeverity.Success, "backed up",
                Meta(("source", d.Source), ("version", d.Version)));
        });
        events.RegisterHandler<InstanceBackupRestoredData>(d =>
        {
            RefreshBackupsOf(d.InstanceName);
            return WriteServer(d, AuditAction.BackupRestore, AuditSeverity.Success, "restored backup for",
                Meta(("source", d.Source), ("version", d.Version)));
        });

        // server.crash — the resident supervisor's autonomous signals (kgsm-watchdog, kgsm-lib 1.9.0),
        // both stamped Actor/Origin = "system" upstream. Per-event policy (action/severity/summary/meta)
        // lives in the pure AuditMapping mappers so it is unit-tested without a live socket (M6·0).
        events.RegisterHandler<InstanceCrashedData>(d =>
        {
            instanceCache.UpdateStatus(d.InstanceName, false);
            PublishLive(AuditMapping.FromCrashEvent(d, options.HostId));
            return Task.CompletedTask;
        });
        events.RegisterHandler<InstanceFailedData>(d =>
        {
            instanceCache.UpdateStatus(d.InstanceName, false);
            PublishLive(AuditMapping.FromFailedEvent(d, options.HostId));
            return Task.CompletedTask;
        });

        // network.ports.open / .close — the CLI-path firewall echoes (kgsm bash emits these on a
        // confirmed open/close via create, firewall-enable/disable, or uninstall — kgsm-lib 1.12.0).
        // Both recorded so the trail is symmetric. The api-issued `open_ports` command writes
        // `network.ports.open` directly at M6·b (kgsm runs nothing → no echo, the auth.* case); there is
        // no api close command (§3·g is open-only), so `network.ports.close` is cleanly CLI-echo-only.
        events.RegisterHandler<InstancePortsOpenedData>(d =>
        {
            PublishLive(AuditMapping.FromPortsOpenedEvent(d, options.HostId));
            return Task.CompletedTask;
        });
        events.RegisterHandler<InstancePortsClosedData>(d =>
        {
            PublishLive(AuditMapping.FromPortsClosedEvent(d, options.HostId));
            return Task.CompletedTask;
        });

        // network.upnp.open / .close — the watchdog's ROUTER-forward echoes (kgsm-lib 1.21.0). Distinct
        // from the firewall ports.* above: the watchdog opens/closes UPnP mappings on the IGD via upnpc
        // on bring-up/stop and emits these (system/system) only on a confirmed upnpc-exit-0 transition.
        // Watchdog-echo-only (no api-issued UPnP command). Pure mapper, socket-free. Engine-owned → no double-write.
        events.RegisterHandler<InstanceUpnpOpenedData>(d =>
        {
            PublishLive(AuditMapping.FromUpnpOpenedEvent(d, options.HostId));
            return Task.CompletedTask;
        });
        events.RegisterHandler<InstanceUpnpClosedData>(d =>
        {
            PublishLive(AuditMapping.FromUpnpClosedEvent(d, options.HostId));
            return Task.CompletedTask;
        });

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
            PublishLive(AuditMapping.FromPlayerJoinedEvent(d, options.HostId));
            return Task.CompletedTask;
        });
        events.RegisterHandler<InstancePlayerLeftData>(d =>
        {
            roster.Leave(d.InstanceName, d.SessionKey, d.PlayerId, d.PlayerName, d.PlayerAddr,
                d.Timestamp ?? DateTimeOffset.UtcNow);
            history.Leave(d.InstanceName, d.SessionKey, d.PlayerId, d.PlayerName, d.PlayerAddr,
                d.Timestamp ?? DateTimeOffset.UtcNow);
            PublishLive(AuditMapping.FromPlayerLeftEvent(d, options.HostId));
            return Task.CompletedTask;
        });

        // config.set — instance config edits (kgsm-lib 1.22.0). The PATCH /servers/{id}/config path stamps
        // actor+origin onto SetInstanceConfigValue, so this echo carries provenance; engine-owned → live
        // publish only (NOT WriteServerAndBridge — a config edit is not a recovery action). KEY ONLY in
        // meta (the event never carries the value — secret hygiene). Pure mapper, socket-free.
        events.RegisterHandler<InstanceConfigChangedData>(d =>
        {
            // A server note spans three keys, so one edit emits three of these. Publish only the body's
            // event; the two attribution keys would triple the same action in the live feed. The
            // monitor-history path (MonitorEventShaping) drops the same pair, so the merged /audit and
            // the live stream can't disagree about what an edit looks like.
            if (!AuditMapping.IsNoteAttributionKey(d.Key))
                PublishLive(AuditMapping.FromConfigChangedEvent(d, options.HostId));
            return Task.CompletedTask;
        });

        // console.input — an arbitrary console command sent to a running native instance (kgsm-lib 1.24.0).
        // The POST /servers/{id}/console path stamps actor+origin onto SendInput, so this echo carries
        // provenance; engine-owned → live publish only (not a recovery action). Unlike config.set, the
        // FULL command text rides in meta (recording what was run — see AuditAction.ConsoleInput).
        events.RegisterHandler<InstanceInputSentData>(d =>
        {
            PublishLive(AuditMapping.FromInputSentEvent(d, options.HostId));
            return Task.CompletedTask;
        });

        // blueprint.write / blueprint.revert — a game's blueprint FILE changed (kgsm-lib 1.43.0). These are
        // the first events whose subject is not an instance: they carry BlueprintName, not InstanceName, so
        // the row's target is the blueprint and its serverId is null. The PUT/DELETE /library/{id}/file path
        // threads actor+origin into the emit, so the echo carries the real admin — engine-owned, no
        // double-write (unlike file.write, which the api direct-writes precisely because kgsm emits nothing
        // for an instance file save).
        //
        // Each handler ALSO busts the blueprint catalog cache, composed in here rather than registered as a
        // second RegisterHandler<BlueprintUpdatedData> — kgsm-lib's EventService keeps ONE handler per event
        // type (a plain dictionary indexer), so a second registration would silently REPLACE this audit
        // write instead of adding to it (the same trap documented on the start/stop roster resets above).
        // Busting from the event rather than from the controller is what makes an ASSISTANT-originated
        // blueprint write invalidate the cache too — a post-PUT bust would only ever catch the web editor.
        events.RegisterHandler<BlueprintCreatedData>(d =>
        {
            blueprintCache.TryRefresh();
            PublishLive(AuditMapping.FromBlueprintCreatedEvent(d, options.HostId));
            return Task.CompletedTask;
        });
        events.RegisterHandler<BlueprintUpdatedData>(d =>
        {
            blueprintCache.TryRefresh();
            PublishLive(AuditMapping.FromBlueprintUpdatedEvent(d, options.HostId));
            return Task.CompletedTask;
        });
        events.RegisterHandler<BlueprintRemovedData>(d =>
        {
            blueprintCache.TryRefresh();
            PublishLive(AuditMapping.FromBlueprintRemovedEvent(d, options.HostId));
            return Task.CompletedTask;
        });

        // install phase progression — surface sub-phases via job.patch so connected clients can show
        // granular progress on phantom cards. No audit row: these are transient UI signals, not domain
        // facts. The job's Blueprint was stamped on the in-flight record by CommandRunner.StartInstall
        // and is copied through each `with` expression, so every phase frame carries it. Handlers are
        // no-ops when no in-flight install job exists for the instance (e.g. a manual kgsm install from
        // the CLI that bypasses the API — honest, no phantom was created, no phase update is needed).
        events.RegisterHandler<InstanceInstallationStartedData>(d => PublishPhase(d.InstanceName, "preparing"));
        events.RegisterHandler<InstanceDownloadStartedData>(d => PublishPhase(d.InstanceName, "downloading"));
        events.RegisterHandler<InstanceDeployStartedData>(d => PublishPhase(d.InstanceName, "deploying"));
    }

    private Task PublishPhase(string instanceName, string phase)
    {
        Job? job = jobRegistry.InFlightFor(instanceName);
        if (job is not null)
        {
            Job patched = jobRegistry.Update(job with { Phase = phase });
            hub.Publish(StreamProtocol.JobsTopic, StreamProtocol.JobEntityKey(patched.Id),
                new StreamMessage(StreamProtocol.JobsTopic, StreamProtocol.JobPatch, patched));
        }
        return Task.CompletedTask;
    }

    // Re-scan one instance's backups off the event path. Deliberately fire-and-forget: the scan spawns a
    // kgsm process, and the audit push must not wait on it — nor fail if it fails, since the cache keeps
    // its prior reading and the next scheduled scan corrects it either way.
    private void RefreshBackupsOf(string? instanceName)
    {
        if (string.IsNullOrWhiteSpace(instanceName)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await backupCache.RefreshInstanceAsync(instanceName, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "post-backup rescan for {Instance} failed; the scheduled scan will correct it.",
                    instanceName);
            }
        });
    }

    private Task WriteServer(
        EventDataBase data, string action, string severity, string verb,
        IReadOnlyDictionary<string, string>? meta = null)
    {
        PublishLive(AuditMapping.FromServerEvent(data, action, severity, verb, options.HostId, meta));
        return Task.CompletedTask;
    }

    // Publish a server.start/server.restart row live, then — only if it is a RECOVERY action — hand its
    // id to the alert engine: a crash that clears because THIS recovery brought the server back links to
    // it (resolution.actionId — M6·a). A stop is not a recovery (it never reaches here, separate handler).
    // The bridge only needs an id that is INTERNALLY consistent with what GET /audit will later show for
    // the same event (the deterministic AuditId.ForEvent, captured via idTracker) — it does not need a
    // DB round-trip, since AuditWrite.Ts already equals the value a persisted row's Ts would have been.
    private Task WriteServerAndBridge(EventDataBase data, string action, string verb)
    {
        AuditRecord row = PublishLive(
            AuditMapping.FromServerEvent(data, action, AuditSeverity.Info, verb, options.HostId));
        if (IsRecoveryAction(data, action) && !string.IsNullOrEmpty(data.InstanceName))
            alerts.NoteRecoveryAction(data.InstanceName, row.Id, row.Ts);
        return Task.CompletedTask;
    }

    // Shape + publish (never persist) one engine-sourced audit row: tags it with the deterministic id
    // captured by idTracker's raw handler for this exact event, then fans it out via
    // AuditService.PublishLive (audit WS topic + notifications — see that method's remarks).
    private AuditRecord PublishLive(AuditWrite write)
    {
        AuditRecord record = AuditMapping.ToRecordDirect(write, idTracker.TakePendingId(logger));
        audit.PublishLive(record);
        return record;
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
