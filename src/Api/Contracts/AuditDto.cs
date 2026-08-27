namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// The identity an action ran as (architecture.html §3·d <c>actor</c>). <see cref="Kind"/> is
/// <c>user|system|token</c>; <see cref="Provider"/> the identity source (<c>discord|system|api</c>,
/// nullable). The pair with <see cref="AuditRecord.Origin"/> answers both <em>whose authority</em>
/// (this) and <em>through which surface</em> (origin) — never collapsed (the user's actor-vs-origin
/// requirement).
/// </summary>
public sealed record AuditActor(string Kind, string Name, string? Provider);

/// <summary>What an action acted on (architecture.html §3·d <c>target</c>). Null when the action is
/// panel-wide (no target).</summary>
public sealed record AuditTarget(string Kind, string Id, string? Name);

/// <summary>
/// One audit record — the wire shape of an append-only action fact (architecture.html §3·d). Emitted
/// by <c>GET /audit</c> (a page element) and pushed on the <c>audit</c> WS topic as <c>audit.append</c>.
/// </summary>
/// <param name="Id">Opaque, stable, public event id (<c>evt_…</c>).</param>
/// <param name="Ts">When it happened (ISO-8601 UTC <c>Z</c>).</param>
/// <param name="Origin">The driving surface (<c>ui|assistant|discord|system|api</c>) or
/// <see langword="null"/> — a §6 divergence from the doc's NOT-NULL <c>origin</c>: a direct-CLI engine
/// action has no surface, so null (never fabricated).</param>
/// <param name="Actor">Whose authority it carried.</param>
/// <param name="Action">The closed dotted vocabulary (<see cref="AuditAction"/>).</param>
/// <param name="Severity">Display weight (<see cref="AuditSeverity"/>).</param>
/// <param name="Target">What it acted on, or null.</param>
/// <param name="ServerId">Denormalized scope key for <c>?serverId=</c>; null if none.</param>
/// <param name="HostId">Denormalized scope key (this host) for host scoping.</param>
/// <param name="Summary">Human one-line.</param>
/// <param name="Meta">Free-form, action-specific detail (string-valued for now), or null.</param>
/// <param name="Outcome">
/// How it went (<see cref="AuditOutcome"/>), or <see langword="null"/> when the producer did not say.
/// Separate from <see cref="Severity"/> and answering a different question: a backup created and a
/// config key set are both routine and differ here, where an uninstall that worked and one that
/// failed differ in weight. Additive, so a client that does not read it is unaffected.
/// </param>
public sealed record AuditRecord(
    string Id,
    DateTimeOffset Ts,
    string? Origin,
    AuditActor Actor,
    string Action,
    string Severity,
    AuditTarget? Target,
    string? ServerId,
    string? HostId,
    string Summary,
    IReadOnlyDictionary<string, string>? Meta,
    string? Outcome = null);

/// <summary>
/// A keyset page of audit records (architecture.html §6 cursor pagination): <c>{ data, nextCursor }</c>,
/// newest first. <see cref="NextCursor"/> is an opaque cursor string — pass it back as <c>?cursor=</c>
/// for the next page — or <see langword="null"/> when there are no older rows. As of
/// The page is a ts-DESC merge of the API's own local rows (auth/session/
/// leaf/files/console-audit — never engine-sourced) and kgsm-monitor's engine event history (shaped at
/// read time); <see cref="NextCursor"/>'s internal encoding changed accordingly (a composite
/// <c>(ts, id)</c> keyset spanning both sources, was a bare local <c>rowid</c>) but stays opaque to the
/// client — kgsm-web only ever stores and echoes it back, never parses it.
/// </summary>
/// <param name="EngineHistoryDegraded">
/// <see langword="true"/> when kgsm-monitor was unreachable for this page, so it contains ONLY the
/// API's own local rows — an honest partial, never a silent drop of the engine history. Additive field
/// (architecture.html invariant #4); absent/false on a healthy read, so an unmodified older client
/// simply never notices it.
/// </param>
/// <param name="Journals">
/// What each producer's event journal contributed, or <see langword="null"/> when the engine is
/// unprovisioned and none was read. Additive (architecture.html invariant #4), so an unmodified client
/// simply never notices it.
/// <para>
/// The ecosystem records events per producer — the engine writes what the engine did, each leaf writes
/// what it did — and this page is their merge. <see cref="EngineHistoryDegraded"/> stays the answer to
/// "can this page show engine history at all", which is what the banner in the panel is about; this
/// says which individual producers answered, so a page missing one leaf's rows can say which leaf
/// rather than looking complete.
/// </para>
/// </param>
public sealed record AuditPage(
    IReadOnlyList<AuditRecord> Data,
    string? NextCursor,
    bool EngineHistoryDegraded = false,
    IReadOnlyList<AuditJournalCoverage>? Journals = null);

/// <summary>
/// What one producer's event journal contributed to an audit page.
/// </summary>
/// <param name="Producer">The producer id — <c>kgsm</c> for the engine, <c>kgsm-&lt;leaf&gt;</c> otherwise.</param>
/// <param name="Readable">
/// False when that journal was absent or could not be read. A leaf that has never written an event has
/// no journal directory yet, which reads as unreadable and is honest: this API cannot tell "recorded
/// nothing" from "cannot be read", and must not present the first as though it had checked.
/// </param>
/// <param name="CoverageFrom">The oldest moment that journal can still answer for, or null if it holds nothing.</param>
/// <param name="Truncated">True when the scan of that journal stopped at its byte budget.</param>
public sealed record AuditJournalCoverage(
    string Producer,
    bool Readable,
    DateTimeOffset? CoverageFrom,
    bool Truncated);

/// <summary>
/// The closed, server-defined action vocabulary (architecture.html §3·d). Clients and the model can't
/// invent one; an unknown action is rejected at write time. The subset wired in M5 is what the engine
/// event stream + API-internal auth actions can honestly source today.
/// </summary>
public static class AuditAction
{
    // server.* — sourced from kgsm lifecycle events (the engine owns these; no API double-write).
    public const string ServerStart = "server.start";

    // server.ready — the watchdog's readiness signal, and its own action rather than a refinement of
    // server.start. The two report different moments: the process spawned, and the game finished
    // loading and will accept a connection. The gap between them is minutes on a big world, and it is
    // exactly the span somebody asking "when could people actually get in" is looking for.
    public const string ServerReady = "server.ready";

    public const string ServerStop = "server.stop";
    public const string ServerRestart = "server.restart";
    public const string ServerUpdate = "server.update";
    public const string ServerUpdateAvailable = "server.update_available";
    public const string ServerInstall = "server.install";
    public const string ServerUninstall = "server.uninstall";

    // server.move — the instance's files are on a different disk. Its own action rather than a
    // configuration change, because nothing about the server changed: the same instance, the same
    // version, the same world, in a different place. The row names BOTH libraries, since a reader
    // that learns only the destination cannot tell which disk just got its space back — and getting a
    // disk empty before it is unplugged is the whole reason the verb exists.
    public const string ServerMove = "server.move";

    // server.rename — the label somebody reads the server by changed. Nothing was renamed on disk: the
    // instance id is immutable, so this row's target and serverId are the same string they were before
    // and after, and every earlier row about this server still joins to it. That is exactly why the
    // action exists — a feed showing only the current label would silently rewrite its own history, and
    // a reader who remembers "Sunday Server" needs the line that says what it is called now. The row
    // carries both labels in `meta`; kgsm emits its own config.changed for the same write, and
    // that one is dropped so a rename reads as one line rather than two.
    public const string ServerRename = "server.rename";

    // server.crash — the resident supervisor's autonomous crash signals (kgsm-watchdog, kgsm-lib
    // 1.9.0). Wired in M6·0: both InstanceCrashed (auto-restarting, warn) and InstanceFailed
    // (retries exhausted, danger) map to this single doc-given action, distinguished by severity +
    // summary + the restart count. Stamped Actor/Origin = "system" upstream (no human surface).
    public const string ServerCrash = "server.crash";

    // backup.* — sourced from kgsm backup events.
    public const string BackupCreate = "backup.create";
    public const string BackupRestore = "backup.restore";

    // The two removal actions. Separate because they answer different questions: a delete is an
    // operator naming one snapshot, a prune is retention policy sweeping whatever fell outside the
    // keep window. Collapsing them would make "who threw away that backup" a question about counts,
    // and force anyone auditing retention to filter out hand-deletes.
    public const string BackupDelete = "backup.delete";
    public const string BackupPrune = "backup.prune";

    // Retention is a policy an operator revises, and both directions are recorded. They are one action
    // because the question is "who changed what rotation may take", and the summary carries the
    // direction — but the unpin is the half that can cost data later, so it is the louder severity.
    public const string BackupPin = "backup.pin";
    public const string BackupUnpin = "backup.unpin";

    // The one backup action with NO kgsm event behind it — the engine serves no bytes, so this is a
    // direct write (the auth.*/file.write pattern, no double-write risk). Recorded when the archive is
    // authorised to leave the host, not when the click happened.
    public const string BackupDownload = "backup.download";

    // network.* — the firewall door. An instance's ports are open exactly while it runs, so the pair
    // brackets that lifetime: opened on the bring-up, closed on the stop, and closed again when an
    // operator drops firewall management. Both are engine echoes — emitted by the supervisor for the
    // instances it supervises and by kgsm for the edges it performs itself — so the api writes neither
    // directly and there is no double-write risk. Recording closes keeps the trail symmetric.
    public const string NetworkPortsOpen = "network.ports.open";
    public const string NetworkPortsClose = "network.ports.close";

    // network.upnp.* — the ROUTER door, distinct from the firewall (host) door above. The kgsm-watchdog
    // forwards/removes each native instance's ports on the local IGD via upnpc on bring-up/stop and emits
    // network.upnp.opened/.closed (kgsm-lib 1.21.0), stamped system/system (an autonomous daemon action).
    // A separate action pair (not reusing network.ports.*) because a router NAT forward is a different
    // fact from a ufw rule — a host can have one without the other, and conflating them would erase that.
    // Watchdog-echo-only (no api-issued UPnP command); the frontend accepts unknown actions forward-compat.
    public const string NetworkUpnpOpen = "network.upnp.open";
    public const string NetworkUpnpClose = "network.upnp.close";

    // network.upnp.reassert — a forward the ROUTER dropped on its own, put back by the watchdog's sweep
    // while the instance kept running. Its own action rather than a second network.upnp.open because the
    // two answer different questions: an open sits next to a start, this one says the mapping went
    // missing with nothing on this host asking for it. It is the only signal an operator gets that their
    // router discards mappings it accepted — a router can report a lease as infinite and drop it anyway —
    // and how often, which is exactly what a reader filtering this action wants to count.
    public const string NetworkUpnpReassert = "network.upnp.reassert";

    // player.* — presence echoes. kgsm raises player.joined/.left (kgsm-lib 1.19.0); for our
    // container images the kgsm-watchdog forwards them from the in-image detection shim, stamped
    // system/system. Distinct join/leave actions mirror server.start/server.stop. Engine-owned (no API
    // double-write); the player identity (id/name, either nullable) rides in meta, the action is scoped
    // to the server (no player target kind). Beyond the doc's vocabulary — now honestly sourceable
    // (player-presence Increment 1); the frontend accepts unknown actions forward-compat.
    public const string PlayerJoin = "player.join";
    public const string PlayerLeave = "player.leave";

    // player.kick/ban/unban — moderation echoes, sourced from kgsm's player.kicked/.banned/
    // .unbanned (kgsm-lib 2.1.0). Distinct from player.leave: a leave is an observation, these are
    // deliberate operator actions, and a reader asking "who was banned here" must not have to infer
    // intent from a disconnect reason. Engine-owned echo (no API double-write — the moderation
    // endpoints stamp actor+origin onto the kgsm call so this echo carries who did it). The target
    // and the resolved command ride in meta; the row is scoped to the server, like the presence pair.
    public const string PlayerKick = "player.kick";
    public const string PlayerBan = "player.ban";
    public const string PlayerUnban = "player.unban";

    // config.set — sourced from config.changed (kgsm-lib 1.22.0). KEY ONLY in meta; the value is
    // never carried (secret hygiene). Engine-owned echo (no double-write — the PATCH /servers/{id}/config
    // path already stamps actor+origin onto SetInstanceConfigValue).
    public const string ConfigSet = "config.set";

    // console.input — sourced from console.input.sent (kgsm-lib 1.24.0): an arbitrary console command was
    // delivered to a running NATIVE instance. Engine-owned echo (no double-write — the POST /servers/{id}/
    // console path stamps actor+origin onto SendInput so the echoed event carries provenance). Unlike
    // config.set's key-only rule, the FULL command text rides in meta on purpose — the trail's value is
    // recording exactly what an operator ran (console commands are admin-level: ban/kick/op/…); a command
    // can contain a secret, accepted because the surface is operator-gated.
    public const string ConsoleInput = "console.input";

    // file.write — API-internal (the file browser saves an instance file; kgsm runs nothing and emits no
    // event, so this is written DIRECTLY, the auth.* case — no echo, no double-write risk). meta carries
    // the path/size/sha256 ONLY, NEVER the content (configs hold rcon passwords/tokens/webhook URLs).
    public const string FileWrite = "file.write";

    // blueprint.* — a game's blueprint file was written or reverted. ENGINE-OWNED echo, unlike file.write
    // above: kgsm emits blueprint.created/.updated/.removed for these, so the row arrives through the event
    // path and the API never direct-writes one. The distinction matters because a write to a SHIPPED
    // blueprint is impossible — it always lands as a user-directory override — so blueprint.write records
    // "this host's copy of <game> was edited" and blueprint.revert records that copy being dropped in favor
    // of the shipped one again. meta carries name/tier/runtime/overridesSystem, NEVER the file content or a
    // diff (a blueprint can carry credentials in its launch arguments — same rule as file.write).
    public const string BlueprintWrite = "blueprint.write";
    public const string BlueprintRevert = "blueprint.revert";

    // library.* — a named placement root was registered, renamed or deregistered. The subject is a
    // library, never a server: a root is registered before anything lives in it and survives every
    // instance leaving it, so these rows carry no serverId.
    //
    // Split by who can honestly say it happened. kgsm emits library.added/library.removed, so add and
    // remove arrive as engine echoes and this API writes neither. A RENAME touches only the registry and
    // the marker and the engine emits nothing for it, so library.rename is a direct write — the only
    // record it can have. library.failed is the same case as command.failed: a refused or broken
    // mutation exits non-zero and emits nothing, and a removal refused because instances still live
    // there is precisely the row an operator goes looking for afterwards.
    public const string LibraryAdd = "library.add";
    public const string LibraryRemove = "library.remove";
    public const string LibraryRename = "library.rename";
    public const string LibraryFailed = "library.failed";

    // assistant.* — what the assistant leaf reports about its own conduct, and deliberately NOT a
    // record of what it did. Every action it performs runs through kgsm with provenance attached, so
    // the engine's own events already carry them attributed to the person who asked; a second copy here
    // would be an answer able to disagree with the engine's. These are the opposite — the turn that did
    // NOT act, which leaves the engine's record empty because from its side nothing occurred.

    // Somebody reached for an action their tier does not carry. The one genuinely security-relevant
    // member of the set: it exists nowhere else on the host, because a refusal goes back to the model as
    // a tool result and stops there. Warn rather than info — nothing broke, and somebody tried something
    // they could not do.
    public const string AssistantActionDeclined = "assistant.action.declined";

    // A mutation is staged and waiting on a person. Nothing has run, so the engine has no row until
    // somebody confirms — and one that expires unapproved produces none at all.
    public const string AssistantActionProposed = "assistant.action.proposed";

    // The model described an action it never took, or a lookup it never made, and was re-prompted or
    // corrected. A record of the assistant's own honesty rather than of anything done to a server, which
    // is why it carries no target.
    public const string AssistantClaimCorrected = "assistant.claim.corrected";

    // A blueprint-authoring run concluded. Distinct from blueprint.write, which reports a FILE appearing:
    // this reports a RUN ending, and on a failed run it is the only row either way. Its probe rides in
    // meta, which is what ties it to the twenty-odd install/uninstall rows the engine wrote for it.
    public const string AssistantBlueprintAuthored = "assistant.blueprint.authored";

    // auth.* — API-internal (no kgsm event → written directly, no double-write risk).
    public const string AuthLogin = "auth.login";
    public const string AuthLogout = "auth.logout";

    // auth.session.* — M4·c Increment 6, the revocation surface. Same direct-write posture as
    // auth.login/.logout (no kgsm event, no double-write). meta carries the revoked `sid`, and for
    // the admin action also the target `userId` (the caller's own identity is the audit row's actor).
    // revoke/.revoke.all are self-service (info — a user managing their own sessions is routine);
    // .revoke.admin is an admin acting on ANOTHER user's session (warn — the substantial-power case
    // D4 flagged, worth a louder trail entry than a routine self-revoke).
    public const string AuthSessionRevoke = "auth.session.revoke";
    public const string AuthSessionRevokeAll = "auth.session.revoke.all";
    public const string AuthSessionRevokeAdmin = "auth.session.revoke.admin";

    // auth.cluster_session — the cluster SSO vouch (POST /auth/cluster-session): a peer node presents a
    // valid cluster service token and asserts an already-authenticated user's identity; this node mints
    // its OWN native session for that user. Same direct-write posture as auth.login (no kgsm event, no
    // double-write) — the vouching peer's node id rides in meta (the closed origin vocabulary has no
    // per-node value, so Origin stays "api" and the node id is the forensic detail).
    public const string AuthClusterSession = "auth.cluster_session";

    // user.* — API-internal admin actions on a KGSM account (the host's own identity store). kgsm runs
    // nothing and emits no event, so these are DIRECT writes, the auth.* case — no echo, no
    // double-write risk. They are privilege events and the trail's most sensitive rows: a tier change
    // is somebody's authority changing, and with the account store as the sole authority it is the ONLY
    // way anyone's authority ever changes. meta carries the target `userId` and the before/after tier
    // or status; never a password, in any form, hashed or otherwise.
    public const string UserProvision = "user.provision";
    public const string UserApprove = "user.approve";
    public const string UserDisable = "user.disable";
    public const string UserTierChange = "user.tier_change";
    public const string UserDelete = "user.delete";

    // user.password — a password was set or changed. Recorded because losing the ability to see that
    // someone else set your password is losing the only signal an account takeover leaves. meta names
    // whose account it was and whether the holder or an admin did it — never the password.
    public const string UserPassword = "user.password";

    // identity.* — an external identity was attached to or detached from an account. A link is a
    // privilege event: afterwards, whoever controls that provider account can sign in as this one.
    public const string IdentityLink = "identity.link";
    public const string IdentityUnlink = "identity.unlink";

    // service.* — API-internal admin actions on a leaf service (the leaf-runtime-provisioning/config
    // feature). kgsm runs nothing and emits no event, so these are DIRECT writes (the auth.* case — no echo,
    // no double-write). connect/disconnect flip a leaf's runtime provisioning; config applies a config
    // override (its meta lists the changed keys + outcome — NEVER a secret value).
    public const string ServiceConnect = "service.connect";
    public const string ServiceDisconnect = "service.disconnect";
    public const string ServiceConfig = "service.config";

    // service.restart — a leaf's unit was restarted on its own, rather than as the tail of a config apply
    // (which is service.config, and says so). Its own action because the two answer different questions:
    // a config row explains what changed, this one records that somebody interrupted a running service and
    // nothing about the host's configuration is different afterwards. Written whether systemd accepted it
    // or refused — a refused restart is exactly the case nobody was watching a screen for.
    public const string ServiceRestart = "service.restart";

    // command.* — a command this API issued that ended without doing the thing. The one part of the
    // write path with no echo to ride: kgsm emits an event when a verb WORKS, and a verb that fails,
    // is refused or never runs exits non-zero and says nothing, so these facts exist in no other
    // record on the host. Written directly, the auth.*/file.write case.
    //
    // THREE actions rather than one carrying the outcome in meta, because they are three different
    // questions. A failure is a fault to chase; a capacity refusal is a fleet that is full and says
    // nothing about the instance; a cancellation is somebody deliberately calling off queued work.
    // Collapsing them would make "what broke here" a question you have to filter an outcome field to
    // ask, and would put a refusal in the same bucket as a fault — the exact reading the engine's own
    // exit code exists to prevent.
    //
    // ⚠ The SUCCESS path stays untouched: a verb that works is kgsm's event, audited from the echo
    // with the provenance the command stamped onto it. And the two verbs whose failure the engine
    // reports itself — update (server.update.failed) and uninstall (server.uninstall.failed) —
    // write nothing here, because a second row for a fact a producer already emits is undedupable.
    public const string CommandFailed = "command.failed";
    public const string CommandRefused = "command.refused";
    public const string CommandCancelled = "command.cancelled";

    // host.threshold.* — a measured value crossed a line this host watches, and later came back. Recorded
    // from the episodes kgsm-monitor keeps: the monitor establishes the fact against every sample it takes,
    // and this API transcribes its durable record rather than deciding anything, which is why the rows carry
    // `system:monitor` as their actor rather than a bare `system`. Written directly (the monitor emits no
    // kgsm event), the auth.*/file.write case — no echo, no double-write risk.
    //
    // TWO actions, not one with a changing state: a breach and a recovery are separate immutable facts, and
    // a single row that later mutated would break append-only. The live, mutable view of the same condition
    // is the alert feed, which is a different surface answering a different question.
    public const string HostThresholdBreach = "host.threshold.breach";
    public const string HostThresholdClear = "host.threshold.clear";
}

/// <summary>Display weight for an audit record (architecture.html §3·d <c>severity</c>).</summary>
public static class AuditSeverity
{
    public const string Info = "info";
    public const string Success = "success";
    public const string Warn = "warn";
    public const string Danger = "danger";
}

/// <summary>Every severity spelling this API will pass on, so an unknown one can be dropped.</summary>
/// <remarks>
/// A producer's line is the authority for its own weight, but only for a value this vocabulary
/// defines. A spelling nothing here knows is dropped rather than forwarded: putting it on the wire
/// would make every client guess, where the type-derived fallback is a real answer.
/// </remarks>
public static class AuditSeverities
{
    /// <summary>The defined spellings.</summary>
    public static readonly IReadOnlyCollection<string> All =
        [AuditSeverity.Info, AuditSeverity.Success, AuditSeverity.Warn, AuditSeverity.Danger];
}

/// <summary>
/// How an event went, separately from how much it matters.
/// </summary>
/// <remarks>
/// Stamped by the producer that raised the event and passed through untouched. Absent means the
/// producer did not say, which is not the same as <see cref="Neutral"/> — a reader distinguishes
/// "reports nothing either way" from "was not asked".
/// </remarks>
public static class AuditOutcome
{
    /// <summary>Reports neither a success nor a failure — it reports a fact.</summary>
    public const string Neutral = "neutral";

    /// <summary>Something completed, and completing was the good result.</summary>
    public const string Success = "success";

    /// <summary>Something did not do what it set out to do.</summary>
    public const string Failure = "failure";
}

/// <summary>Every outcome spelling this API will pass on.</summary>
public static class AuditOutcomes
{
    /// <summary>The defined spellings.</summary>
    public static readonly IReadOnlyCollection<string> All =
        [AuditOutcome.Neutral, AuditOutcome.Success, AuditOutcome.Failure];
}


/// <summary>Actor kinds (architecture.html §3·d <c>actor.kind</c>).</summary>
public static class ActorKind
{
    public const string User = "user";
    public const string System = "system";
    public const string Token = "token";
}

/// <summary>Identity providers (architecture.html §3·d <c>actor.provider</c>).</summary>
public static class ActorProvider
{
    public const string Discord = "discord";
    public const string System = "system";
    public const string Api = "api";

    // A KGSM account signed in with its own password — no external provider involved. Distinct from
    // "api" (a token, not a person) and from "system" (nobody). Beyond the doc's set; the frontend
    // accepts unknown providers forward-compat.
    public const string Local = "local";
}

/// <summary>Target kinds (architecture.html §3·d <c>target.kind</c>).</summary>
public static class AuditTargetKind
{
    public const string Server = "server";
    public const string Host = "host";
    // A KGSM leaf service (monitor/watchdog/assistant/firewall) — the target of the service.* admin actions
    // (the leaf-runtime-provisioning/config feature). Beyond the doc's server/host set; the frontend accepts
    // unknown target kinds forward-compat.
    public const string Leaf = "leaf";
    // A game blueprint (the target of the blueprint.* actions). Its id is the blueprint name, which is NOT
    // a server id — a blueprint is the template installed servers are created from, so these rows carry no
    // serverId and must never be read as being about an instance.
    public const string Blueprint = "blueprint";
    // A named placement root (the target of the library.* actions). Its id is the library name, which is
    // NOT a server id — a library holds servers without being one — so these rows carry no serverId and
    // must never be read as being about an instance.
    public const string Library = "library";
}

/// <summary>
/// The closed origin set (architecture.html §3·d). Two values are <b>reserved</b> and no request may
/// declare either: <see cref="System"/> for the engine/watchdog path (stamped at the kgsm level via
/// <c>KGSM_EVENT_ORIGIN</c>; the API never emits it), and <see cref="Notification"/>, which this API
/// stamps itself when it redeems a notification button. <see cref="IsCallerDeclarable"/> is the subset a
/// request may name.
/// </summary>
public static class AuditOrigin
{
    public const string Ui = "ui";
    public const string Assistant = "assistant";
    public const string Discord = "discord";
    public const string System = "system";
    public const string Api = "api";

    /// <summary>
    /// A button on a push notification, tapped without the panel open.
    /// <para>
    /// It is a surface of its own rather than a flavour of <see cref="Ui"/>, which is what origin is
    /// for — the same reason <see cref="Discord"/> is here. A person answering from a lock screen has a
    /// notification's worth of context and no page in front of them, and reading back later that an
    /// update was applied that way is a materially different fact from a click in the panel.
    /// </para>
    /// <para>
    /// It names the notification, not the device: these buttons render on a desktop browser as readily
    /// as on a phone, and the panel installed to a home screen stamps <see cref="Ui"/> for everything
    /// done inside it. So the distinction here is notification-versus-panel, never phone-versus-laptop.
    /// </para>
    /// </summary>
    public const string Notification = "notification";

    /// <summary>True if <paramref name="origin"/> is one of the closed set (used to normalize an event's
    /// origin; an unrecognized value is treated as null — never fabricated). ⚠ A value stamped on an
    /// engine call but missing here comes back off the echo as <see langword="null"/>: this is the gate
    /// the whole provenance passes through, not a display list.</summary>
    public static bool IsKnown(string? origin) =>
        origin is Ui or Assistant or Discord or System or Api or Notification;

    /// <summary>True if a client may declare <paramref name="origin"/> on the command path — everything
    /// except the two this host stamps for itself. A caller naming <see cref="Notification"/> would be
    /// claiming to be a redemption this API performed, which is exactly the claim it cannot check.</summary>
    public static bool IsCallerDeclarable(string origin) =>
        origin is Ui or Assistant or Discord or Api;
}
