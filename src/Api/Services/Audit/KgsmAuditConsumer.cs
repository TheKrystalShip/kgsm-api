using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Realtime;
using TheKrystalShip.Api.Services.Aggregation;
using TheKrystalShip.Api.Services.Alerts;
using TheKrystalShip.Api.Services.Commands;
using TheKrystalShip.Api.Services.Players;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.Api.Services.Audit;

/// <summary>
/// Subscribes to the kgsm event stream (via kgsm-lib's <see cref="IEventService"/> — the C#↔engine
/// chokepoint, never a raw socket) and turns each lifecycle event into a live <c>audit</c> WS push +
/// (for start/restart) the alert↔audit recovery bridge. This
/// consumer no longer <b>persists</b> engine-sourced rows: kgsm-monitor is the single source of truth
/// for engine history (it persists the raw envelope neutrally; <c>GET /audit</c> merges it in at read
/// time, shaped via <see cref="AuditMapping"/>/<see cref="EngineEventShaping"/>). This consumer stays
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
    Aggregation.BackupCache backupCache,
    ApiOptions options,
    StreamHub hub,
    Realtime.DomainPump domainPump,
    JobRegistry jobRegistry,
    ILogger<KgsmAuditConsumer> logger) : IHostedService
{
    // Captures the journal-position id for each engine envelope (via a RegisterRawHandler
    // hook — see EngineEventIdTracker remarks) so the typed handlers below, which only ever receive the
    // typed EventDataBase, can still tag their published-but-not-persisted rows with the SAME id
    // kgsm-monitor independently computed for the identical event.
    private readonly EngineEventIdTracker idTracker = new();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Always ensure the audit table exists — it holds this host's pre-cutover history, which
        // GET /audit still merges whether or not an engine is installed.
        await audit.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);

        // Create this API's own journal directory now rather than on its first sign-in. A reader
        // discovers a producer by finding its journal, so an API that has simply recorded nothing yet
        // would otherwise be indistinguishable from one that writes no journal at all — and would stay
        // invisible to every reader on the host until both an event fired and each of them restarted.
        try
        {
            Directory.CreateDirectory(options.EventJournalDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not fatal: the writer retries on every append and says so if it still cannot.
            logger.LogWarning(ex, "Audit: could not create the event journal directory at {Dir}",
                options.EventJournalDir);
        }

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
            logger.LogInformation("Audit: reading kgsm events from the journal at {Journal}", options.KgsmJournalDir);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Audit: failed to initialize the kgsm event listener — "
                + "engine-sourced audit is off.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// The types this API writes to its own journal, and therefore the ones it publishes live off the
    /// raw hook rather than through a typed handler.
    /// </summary>
    private static readonly HashSet<string> OwnEventTypes = new(StringComparer.Ordinal)
    {
        ApiJournal.LoginEvent, ApiJournal.LogoutEvent, ApiJournal.ClusterSessionEvent,
        ApiJournal.SessionRevokedEvent,
        ApiJournal.UserProvisionedEvent, ApiJournal.UserApprovedEvent, ApiJournal.UserDisabledEvent,
        ApiJournal.UserTierChangedEvent, ApiJournal.UserDeletedEvent, ApiJournal.UserPasswordChangedEvent,
        ApiJournal.IdentityLinkedEvent, ApiJournal.IdentityUnlinkedEvent,
        ApiJournal.ServiceConnectedEvent, ApiJournal.ServiceDisconnectedEvent,
        ApiJournal.ServiceConfigChangedEvent, ApiJournal.ServiceRestartedEvent,
        ApiJournal.FileWrittenEvent, ApiJournal.BackupDownloadedEvent,
        ApiJournal.CommandFailedEvent, ApiJournal.CommandRefusedEvent, ApiJournal.CommandCancelledEvent,
        ApiJournal.LibraryRenamedEvent, ApiJournal.LibraryFailedEvent,
    };

    /// <summary>
    /// Engine failures that are facts but belong to no single operation, so this API gives them no
    /// domain action of its own — they are published live in the SAME generic shape the history read
    /// gives them, off the same hook and the same shaping call.
    /// </summary>
    /// <remarks>
    /// A download or a deploy is a step of an install <em>or</em> of an update, and nothing in the
    /// payload says which — so labelling one <c>server.install</c> would name a parent operation this
    /// API is guessing at. Which run it belonged to is answered by the run's own outcome fact beside it
    /// (<c>instance_update_failed</c>, or the absence of <c>instance_installed</c>).
    /// <para>
    /// What they were missing was not a label but a reader: nothing published them, so a failure — the
    /// event an operator most needs pushed — reached a surface only when somebody happened to refresh.
    /// </para>
    /// </remarks>
    private static readonly HashSet<string> UnattributedFailureTypes = new(StringComparer.Ordinal)
    {
        "instance_download_failed", "instance_deploy_failed",
    };

    /// <summary>
    /// Shape and announce one of this API's own journal events, so a row reaches an open browser as
    /// soon as it is recorded rather than on the reader's next refresh.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The filter is what keeps every other event from being published twice.</b> This hook fires
    /// for every envelope on every journal; the engine's own already have typed handlers that publish
    /// them, so anything outside <see cref="OwnEventTypes"/> is left alone here.
    /// </para>
    /// <para>
    /// The id comes from the journal position, exactly as the history read derives it — so a client
    /// reconciling this live row against a later <c>GET /audit</c> page sees one identity, not two.
    /// The shaping is <see cref="EngineEventShaping"/>'s, the same call the history read makes, so the
    /// two cannot word one row differently.
    /// </para>
    /// </remarks>
    private Task PublishOwnEventAsync(EventWrapper wrapper, EventPosition position)
    {
        if (wrapper is null
            || !(OwnEventTypes.Contains(wrapper.EventType) || UnattributedFailureTypes.Contains(wrapper.EventType)))
            return Task.CompletedTask;

        try
        {
            // Derived from the position directly rather than through the id tracker: that tracker
            // exists because a TYPED handler never sees the envelope, and this hook does. Reading its
            // one-shot field here would also consume a value the typed path is entitled to.
            var entry = new EventHistoryEntry(
                Id: position switch
                {
                    { IsKnown: false } => "evt_" + Guid.NewGuid().ToString("N")[..16],
                    { Producer: { Length: > 0 } producer } =>
                        AuditId.ForLine(position.EventId, producer, position.Segment, position.Offset),
                    _ => AuditId.ForLine(position.EventId, position.Segment, position.Offset),
                },
                Ts: wrapper.Timestamp ?? DateTimeOffset.UtcNow,
                Type: wrapper.EventType,
                Instance: null,
                Blueprint: null,
                Actor: wrapper.Actor,
                Origin: wrapper.Origin,
                Hostname: wrapper.Hostname,
                Data: wrapper.Data,
                Producer: position.Producer);

            if (EngineEventShaping.Shape(entry, options.HostId) is { } record)
                audit.PublishLive(record);
        }
        catch (Exception ex)
        {
            // The event is already recorded — the journal is the record, and GET /audit reads it back.
            // Failing here would lose the live push, never the fact.
            logger.LogWarning(ex, "Audit: could not publish {Type} live", wrapper.EventType);
        }

        return Task.CompletedTask;
    }

    private void RegisterHandlers(IEventService events)
    {
        // Captures the journal-position id for the envelope about to be typed-dispatched — see
        // EngineEventIdTracker + TakePendingEventId. Registered first so it is armed before any typed
        // handler below can run (RegisterRawHandler fires before typed dispatch for every event anyway,
        // but registration order here has no bearing on that — kgsm-lib always runs ALL raw handlers
        // before typed dispatch, regardless of relative registration order).
        events.RegisterRawHandler(idTracker.OnRawEvent);

        // This API's OWN events, published live. They cannot go through RegisterHandler<T> like the
        // engine's: kgsm-lib keys a typed handler on the payload CLASS, and several of these types
        // deliberately share one — auth_login and auth_logout are the same shape, told apart by which
        // type fired. A typed handler would get one registration for both and no way to know which
        // arrived. The raw hook carries the type and the position, which is exactly what is missing.
        events.RegisterRawHandler(PublishOwnEventAsync);

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
            SettleObserved(d.InstanceName);
            domainPump.Nudge();
            return WriteServerAndBridge(d, AuditAction.ServerStart, "started");
        });
        // instance_ready — the watchdog's readiness signal: its log-scrape confirms the game finished
        // booting, distinct from instance_started (the process merely spawned). It is a fact of its own
        // (server.ready), not a refinement of server.start: on a big world the gap between the two is
        // minutes, and it is the moment somebody asking "when could people actually get in" wants.
        // It also closes the starting latch (InstanceCache.MarkReady) so status flips starting ->
        // running; DomainPump's existing Status diff (Realtime/CLAUDE.md) fans that out over SSE.
        events.RegisterHandler<InstanceReadyData>(d =>
        {
            instanceCache.MarkReady(d.InstanceName);
            // starting -> running is a state change like any other: say it now, not up to a poll later.
            domainPump.Nudge();
            return WriteServer(d, AuditAction.ServerReady, AuditSeverity.Info, "finished loading");
        });
        events.RegisterHandler<InstanceStoppedData>(d =>
        {
            instanceCache.UpdateStatus(d.InstanceName, false);
            roster.Reset(d.InstanceName);
            history.Reset(d.InstanceName);
            SettleObserved(d.InstanceName);
            domainPump.Nudge();
            return WriteServer(d, AuditAction.ServerStop, AuditSeverity.Info, "stopped");
        });
        events.RegisterHandler<InstanceRestartedData>(d =>
        {
            // A restart ENDS where a start does — with a process that has just been spawned and has not
            // finished booting — so it opens the same starting window (MarkStarting, not a plain
            // "running" flip). Without it a restart is the one lifecycle path that skips `starting`
            // entirely: the instance reads `running` from the moment its process exists, which is the
            // whole of the boot early (40s for a Project Zomboid world, minutes for a big one), and the
            // operator who just pressed Restart is told the server is up while nobody can connect to it.
            // The window closes on the watchdog's instance_ready, exactly as it does for a start.
            //
            // This is the start half of a restart whoever performed it: kgsm's restart command, the
            // watchdog's own scheduled one and its autonomous crash-recovery respawn all end here, and
            // the down phase before it is instance_restart_stopped (or instance_crashed). One meaning,
            // one handler — a new run is up and booting.
            instanceCache.MarkStarting(d.InstanceName);
            roster.Reset(d.InstanceName);
            history.Reset(d.InstanceName);
            SettleObserved(d.InstanceName);
            domainPump.Nudge();
            return WriteServerAndBridge(d, AuditAction.ServerRestart, "restarted");
        });
        events.RegisterHandler<InstanceUninstalledData>(d =>
        {
            instanceCache.TryRefresh();
            return WriteServer(d, AuditAction.ServerUninstall, AuditSeverity.Warn, "uninstalled");
        });

        // server.update_available — a newer build exists upstream. kgsm establishes this: it records
        // what each check found beside the instance and emits only for a version it has not announced
        // before, so this is one row per new build rather than one per check. The API echoes it like
        // every other engine event and holds no opinion of its own about update availability.
        events.RegisterHandler<InstanceUpdateAvailableData>(d =>
            WriteUpdateAvailable(d));

        // server.update — sourced from the version-changed event (it carries the meaningful old→new
        // detail). A plain instance_updated with no version change produces no row (nothing material
        // changed) — an honest boundary, documented in PLAN §8.
        // The handler also re-reads the roster, because an applied update makes the engine's own
        // "update available" answer go false: the state lives beside the instance, and refreshing is
        // how the cleared value reaches the next server.patch. Re-reading rather than clearing a local
        // copy is the point — a second opinion about update availability is exactly what this API no
        // longer keeps.
        events.RegisterHandler<InstanceVersionUpdatedData>(d =>
        {
            instanceCache.TryRefresh();
            SettleObserved(d.InstanceName);
            return WriteServer(d, AuditAction.ServerUpdate, AuditSeverity.Info, "updated",
                Meta(("oldVersion", d.OldVersion), ("newVersion", d.NewVersion)));
        });

        // instance_update_started / instance_update_finished — kgsm brackets an update run with these
        // whoever drove it, which is what lets a surface show an instance as busy for the whole
        // minutes-long download-and-deploy instead of only learning the version moved at the end.
        // AUDIT-SILENT by design, like instance_ready: server.update (written from the version event
        // above) is the fact worth an append-only row; "a run is in progress" is live state, not history.
        //
        // What they do is claim and release this instance's ONE in-flight job slot, so an update run from
        // the CLI, the assistant or the bot rides the same job record — and the same server.activeJob
        // field — as one this API issued. When this API issued it, its own job already holds the slot: the
        // claim returns null and the release leaves it alone, because CommandRunner settles its own job
        // when the engine process exits, which is the honest end of THAT run.
        events.RegisterHandler<InstanceUpdateStartedData>(d =>
        {
            ObserveStarted(d.InstanceName, CommandVerb.Update);
            return Task.CompletedTask;
        });
        events.RegisterHandler<InstanceUpdateFinishedData>(d =>
        {
            SettleObserved(d.InstanceName);
            return Task.CompletedTask;
        });
        // instance_update_failed — the run ended and the version did not move, for a reason. It arrives
        // BEFORE the bracket's finish, so it is what settles the job, and it settles it as FAILED: the
        // other way an update leaves the version alone is finding nothing to do, and without this fact
        // the two were the same two lines. An engine-driven update that kgsm refused reported itself to
        // every surface as a succeeded job.
        //
        // Unlike the brackets it is NOT audit-silent: a run that did not do what was asked is a fact
        // worth an append-only row, carried on server.update at Danger — the same shape server.crash
        // uses to tell its two facts apart by severity rather than by inventing an action.
        events.RegisterHandler<InstanceUpdateFailedData>(d =>
        {
            SettleObserved(d.InstanceName, "kgsm reported the update as failed");
            domainPump.Nudge();
            return WriteServer(d, AuditAction.ServerUpdate, AuditSeverity.Danger, "could not update");
        });

        // instance_stop_started / instance_stop_finished — the same bracket for a shutdown (kgsm
        // 3.7.3-rc1). A stop is not instant either: the supervisor asks the game to stop and waits for it
        // to drain, which is seconds to a minute for a game that saves its world and the whole stop
        // timeout for one that ignores its stop command. Same discipline as the update pair — audit-silent
        // (server.stop, written from instance_stopped, is the fact), claims and releases the one in-flight
        // slot, and mints nothing when this API issued the stop itself.
        //
        // The finish is BOTH events: instance_stop_finished ends the run whatever its outcome, and
        // instance_stopped (handled above, on success only) settles it too — either is honest evidence
        // the run is over, and whichever lands first releases the slot.
        events.RegisterHandler<InstanceStopStartedData>(d =>
        {
            ObserveStarted(d.InstanceName, CommandVerb.Stop);
            return Task.CompletedTask;
        });
        events.RegisterHandler<InstanceStopFinishedData>(d =>
        {
            SettleObserved(d.InstanceName);
            return Task.CompletedTask;
        });

        // instance_restart_started / instance_restart_finished — the bracket for the longest lifecycle
        // verb (kgsm 3.7.4-rc1): a stop's drain plus the game's whole boot. kgsm runs both halves through
        // its pure logic rather than the stop and start commands, so NOTHING else is emitted in between —
        // without this pair the first and only word is instance_restarted at the very end, and until then
        // the instance still reads as running normally. Same discipline as the other two brackets:
        // audit-silent (server.restart, written from instance_restarted, is the fact), claims and releases
        // the one in-flight slot, and mints nothing when this API issued the restart itself.
        events.RegisterHandler<InstanceRestartStartedData>(d =>
        {
            ObserveStarted(d.InstanceName, CommandVerb.Restart);
            return Task.CompletedTask;
        });
        // instance_restart_stopped — the middle of that bracket: the old run is down and the new one has
        // not been spawned. This is the engine reporting the state rather than this API inferring it, and
        // it is the only word anything gets for the whole shutdown — seconds to a minute, and the full
        // drain of a game that saves its world on the way out. Without it the instance reads as running
        // while its process does not exist, and every surface joined to run-state says so.
        //
        // It carries the roster and the run history down with it for the same reason a stop does: those
        // sessions ended when the process did, and leaving them would show players connected to a server
        // that is not there. Audit-silent like the rest of the bracket (the catalog classifies it Phase),
        // and it deliberately does NOT settle the in-flight job — the restart it belongs to is still
        // running, and releasing the slot here would both drop the surface's "Restarting…" and let the
        // next command in mid-restart.
        events.RegisterHandler<InstanceRestartStoppedData>(d =>
        {
            instanceCache.UpdateStatus(d.InstanceName, false);
            roster.Reset(d.InstanceName);
            history.Reset(d.InstanceName);
            domainPump.Nudge();
            return Task.CompletedTask;
        });
        events.RegisterHandler<InstanceRestartFinishedData>(d =>
        {
            SettleObserved(d.InstanceName);
            return Task.CompletedTask;
        });

        // instance_uninstall_started / _finished — the bracket around a removal. An uninstall stops the
        // game, tears down its integrations and deletes its files, which is not instant for a large one,
        // and until this was registered an engine-driven uninstall claimed no job slot: the panel showed
        // nothing at all while a server was being destroyed, and only an uninstall this API issued was
        // visible. Audit-silent like every other bracket — server.uninstall is the fact.
        events.RegisterHandler<InstanceUninstallStartedData>(d =>
        {
            ObserveStarted(d.InstanceName, CommandVerb.Uninstall);
            return Task.CompletedTask;
        });
        events.RegisterHandler<InstanceUninstallFinishedData>(d =>
        {
            SettleObserved(d.InstanceName);
            return Task.CompletedTask;
        });
        // instance_uninstall_failed — the removal did not happen. Settles the run as failed and writes a
        // row, on the same reasoning as the update's: the instance is still there, and a surface told the
        // run merely "ended" would leave an operator believing the server is gone.
        events.RegisterHandler<InstanceUninstallFailedData>(d =>
        {
            SettleObserved(d.InstanceName, "kgsm reported the uninstall as failed");
            instanceCache.TryRefresh();
            return WriteServer(d, AuditAction.ServerUninstall, AuditSeverity.Danger, "could not uninstall");
        });

        // server.install — carries the blueprint it was installed from, and the library it landed in.
        events.RegisterHandler<InstanceInstalledData>(d =>
        {
            instanceCache.TryRefresh();
            return WriteServer(d, AuditAction.ServerInstall, AuditSeverity.Success, "installed",
                Meta(("blueprint", d.Blueprint), ("library", d.Library)));
        });

        // server.move — the instance's files are on a different disk now. The roster refresh is what
        // makes it matter beyond the row: every absolute path the instance holds was rewritten, so a
        // cached record still names a directory that no longer exists. Named from BOTH libraries,
        // because a drain is asked "which disk is empty now".
        events.RegisterHandler<InstanceMovedData>(d =>
        {
            instanceCache.TryRefresh();
            return WriteServer(d, AuditAction.ServerMove, AuditSeverity.Info, "moved",
                Meta(("fromLibrary", d.FromLibrary), ("toLibrary", d.ToLibrary)));
        });

        // The two backup verbs' brackets. Archiving a large world is minutes of work and the scheduler
        // runs it unattended, so an engine-driven backup or restore claims the one in-flight slot exactly
        // as an update does — otherwise the only backup any surface could show as running was one this
        // API issued itself. Audit-silent: backup.create / backup.restore are the facts.
        events.RegisterHandler<InstanceBackupStartedData>(d =>
        {
            ObserveStarted(d.InstanceName, CommandVerb.BackupCreate);
            return Task.CompletedTask;
        });
        events.RegisterHandler<InstanceBackupFinishedData>(d =>
        {
            SettleObserved(d.InstanceName);
            return Task.CompletedTask;
        });
        events.RegisterHandler<InstanceRestoreStartedData>(d =>
        {
            ObserveStarted(d.InstanceName, CommandVerb.BackupRestore);
            return Task.CompletedTask;
        });
        events.RegisterHandler<InstanceRestoreFinishedData>(d =>
        {
            SettleObserved(d.InstanceName);
            return Task.CompletedTask;
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

        // The removal pair re-scans for the same reason the create/restore pair does — backupCount and
        // lastBackup both change when a backup goes away, and a prune can move lastBackup by deleting
        // the newest thing outside the keep window. Deleting warns (no undo); pruning is policy running
        // to plan, so it informs. The counts carry as meta so a reader sees what the sweep did without
        // diffing two listings.
        events.RegisterHandler<InstanceBackupDeletedData>(d =>
        {
            RefreshBackupsOf(d.InstanceName);
            return WriteServer(d, AuditAction.BackupDelete, AuditSeverity.Warn, "deleted a backup for",
                Meta(("source", d.Source)));
        });
        events.RegisterHandler<InstanceBackupsPrunedData>(d =>
        {
            RefreshBackupsOf(d.InstanceName);
            return WriteServer(d, AuditAction.BackupPrune, AuditSeverity.Info, "pruned backups for",
                // `pinned` is what the sweep protected. Without it, a sweep that removed nothing
                // because everything was pinned reads exactly like one that found nothing to remove.
                Meta(("deleted", d.Deleted.ToString(CultureInfo.InvariantCulture)),
                     ("kept", d.Kept.ToString(CultureInfo.InvariantCulture)),
                     ("pinned", d.Pinned.ToString(CultureInfo.InvariantCulture))));
        });

        // Retention is a policy an operator revises, and both directions are pushed live: the badge
        // on an open backups list is stale the moment either lands, which is why each refreshes the
        // cache the same way a create or a delete does.
        events.RegisterHandler<InstanceBackupPinnedData>(d =>
        {
            RefreshBackupsOf(d.InstanceName);
            return WriteServer(d, AuditAction.BackupPin, AuditSeverity.Info, "pinned a backup for",
                Meta(("source", d.Source)));
        });
        // Warn, like a delete: unpinning is what lets the next sweep take an archive somebody
        // deliberately protected, and it succeeding is exactly what makes it worth surfacing.
        events.RegisterHandler<InstanceBackupUnpinnedData>(d =>
        {
            RefreshBackupsOf(d.InstanceName);
            return WriteServer(d, AuditAction.BackupUnpin, AuditSeverity.Warn, "unpinned a backup for",
                Meta(("source", d.Source)));
        });

        // server.crash — the resident supervisor's autonomous signals (kgsm-watchdog, kgsm-lib 1.9.0),
        // both stamped Actor/Origin = "system" upstream. Per-event policy (action/severity/summary/meta)
        // lives in the pure AuditMapping mappers so it is unit-tested without a live socket (M6·0).
        // Both reset the roster for the same reason the stop handler above does, and the reason is
        // starker here: the process died, so every session it held ended, and a crash emits no
        // per-player "left" line to end them one at a time. instance_failed is the branch nothing else
        // covers — the supervisor has given up, so no restart is coming and no later event will clear
        // these entries.
        events.RegisterHandler<InstanceCrashedData>(d =>
        {
            instanceCache.UpdateStatus(d.InstanceName, false);
            roster.Reset(d.InstanceName);
            history.Reset(d.InstanceName);
            PublishLive(AuditMapping.FromCrashEvent(d, options.HostId));
            return Task.CompletedTask;
        });
        events.RegisterHandler<InstanceFailedData>(d =>
        {
            instanceCache.UpdateStatus(d.InstanceName, false);
            roster.Reset(d.InstanceName);
            history.Reset(d.InstanceName);
            PublishLive(AuditMapping.FromFailedEvent(d, options.HostId));
            return Task.CompletedTask;
        });

        // network.ports.open / .close — the firewall echoes, emitted on a confirmed open/close by
        // whichever component performed it (the supervisor on the instances it supervises, kgsm on the
        // edges it performs itself). Both recorded so the trail is symmetric; neither is ever written
        // directly by the api.
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

        // network.upnp.reassert — the watchdog's sweep found the router had dropped a running instance's
        // forwards and put them back. A fact about the ROUTER rather than about anything on this host,
        // which is why it is not folded into the open above: an operator counting these learns how often
        // their IGD discards mappings it accepted. Engine-owned → no double-write.
        events.RegisterHandler<InstanceUpnpReassertedData>(d =>
        {
            PublishLive(AuditMapping.FromUpnpReassertedEvent(d, options.HostId));
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
            // The server element carries the online count, so a join changes it — announce on the same
            // pass the run-state changes take, or a fleet total sits a poll interval behind the roster
            // frame that already reached the same browser.
            domainPump.Nudge();
            PublishLive(AuditMapping.FromPlayerJoinedEvent(d, options.HostId));
            return Task.CompletedTask;
        });
        events.RegisterHandler<InstancePlayerLeftData>(d =>
        {
            roster.Leave(d.InstanceName, d.SessionKey, d.PlayerId, d.PlayerName, d.PlayerAddr,
                d.Timestamp ?? DateTimeOffset.UtcNow);
            history.Leave(d.InstanceName, d.SessionKey, d.PlayerId, d.PlayerName, d.PlayerAddr,
                d.Timestamp ?? DateTimeOffset.UtcNow);
            domainPump.Nudge();
            PublishLive(AuditMapping.FromPlayerLeftEvent(d, options.HostId));
            return Task.CompletedTask;
        });

        // player.kick / player.ban / player.unban — moderation echoes (kgsm-lib 2.1.0). Engine-owned →
        // the row is written HERE from the echo, never by the endpoint that issued the command (the same
        // no-double-write rule the lifecycle verbs follow). Ban/unban ALSO drive the roster's permanent
        // status, composed here rather than as a second RegisterHandler for the same
        // single-handler-per-type reason as the presence pair above.
        //
        // The roster is keyed on playerIdentity while the event carries the game-facing target token, so
        // the row to move is found by matching that token against the identity fields this server has
        // actually seen. A target that matches nobody (an address banned before it ever connected) still
        // audits — the trail records what the operator did — but moves no roster row, because inventing
        // one would put a player in the roster who was never here.
        events.RegisterHandler<InstancePlayerKickedData>(d =>
        {
            PublishLive(AuditMapping.FromPlayerModerationEvent(
                d, options.HostId, AuditAction.PlayerKick, "kicked"));
            return Task.CompletedTask;
        });
        events.RegisterHandler<InstancePlayerBannedData>(d =>
        {
            string? identity = history.FindIdentityByTarget(d.InstanceName, d.Target);
            if (identity is not null)
                history.Ban(d.InstanceName, identity, reason: null);

            PublishLive(AuditMapping.FromPlayerModerationEvent(
                d, options.HostId, AuditAction.PlayerBan, "banned"));
            return Task.CompletedTask;
        });
        events.RegisterHandler<InstancePlayerUnbannedData>(d =>
        {
            string? identity = history.FindIdentityByTarget(d.InstanceName, d.Target);
            if (identity is not null)
                history.Unban(d.InstanceName, identity);

            PublishLive(AuditMapping.FromPlayerModerationEvent(
                d, options.HostId, AuditAction.PlayerUnban, "unbanned"));
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
            // monitor-history path (EngineEventShaping) drops the same pair, so the merged /audit and
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

        // library.add / library.remove — a named placement root was registered or deregistered. The
        // library CRUD path stamps actor+origin onto the kgsm call, so these echoes carry the real admin;
        // engine-owned, no double-write. A RENAME has no handler here because kgsm emits nothing for it —
        // that row is written to this API's own journal and rides the raw hook above with the rest.
        events.RegisterHandler<LibraryAddedData>(d =>
        {
            PublishLive(AuditMapping.FromLibraryAddedEvent(d, options.HostId));
            return Task.CompletedTask;
        });
        events.RegisterHandler<LibraryRemovedData>(d =>
        {
            PublishLive(AuditMapping.FromLibraryRemovedEvent(d, options.HostId));
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

    // Claim this instance's in-flight slot for an update the ENGINE started (see the handler above).
    // A slot already taken means this API issued the very command the event echoes — its own job is the
    // better record (it knows the actor, the origin and the settle), so nothing is minted.
    private void ObserveStarted(string instanceName, string verb)
    {
        if (string.IsNullOrWhiteSpace(instanceName))
            return;

        Job? job = jobRegistry.TryStartObserved(
            "job_" + Guid.NewGuid().ToString("N")[..8], instanceName, verb, DateTimeOffset.UtcNow);
        if (job is null)
            return;

        logger.LogInformation("observed a kgsm {Verb} of {ServerId} (job {JobId})", verb, instanceName, job.Id);
        PublishJob(job);
    }

    // Release an OBSERVED job's slot. Called on the update's own finish event and on every later engine
    // event that is evidence the run is over (the version moved; the instance started/stopped/restarted) —
    // an observed slot must never outlive the run it describes, because it also gates every subsequent
    // command for that server. A job this API issued is never touched here: CommandRunner settles its own.
    //
    // <paramref name="error"/> is the engine's own failure fact for the run. Without one, Succeeded means
    // "the run ended", not "it worked" — kgsm emits its finish event on every outcome and this API did not
    // run the command, so it has no exit code to claim otherwise. That was honest only while nothing knew
    // better: an update the engine refused settled as a succeeded job on every surface, indistinguishable
    // from one that found nothing to do. Now the engine says which, and a run it reports as failed settles
    // as failed.
    private void SettleObserved(string instanceName, string? error = null)
    {
        if (string.IsNullOrWhiteSpace(instanceName))
            return;

        Job? job = jobRegistry.InFlightFor(instanceName);
        if (job is null || !jobRegistry.IsObserved(job.Id))
            return;

        PublishJob(jobRegistry.Update(job with
        {
            State = error is null ? JobState.Succeeded : JobState.Failed,
            SettledAt = DateTimeOffset.UtcNow,
            Error = error,
        }));
    }

    private void PublishJob(Job job) =>
        hub.Publish(StreamProtocol.JobsTopic, StreamProtocol.JobEntityKey(job.Id),
            new StreamMessage(StreamProtocol.JobsTopic, StreamProtocol.JobPatch, job));

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

    private Task WriteUpdateAvailable(InstanceUpdateAvailableData d)
    {
        PublishLive(AuditMapping.FromUpdateAvailableEvent(d, options.HostId));
        return Task.CompletedTask;
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
