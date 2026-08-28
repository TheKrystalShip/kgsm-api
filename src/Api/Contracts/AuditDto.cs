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
/// <param name="Action">
/// What happened, named the way the producer that recorded it names the event — an open vocabulary,
/// dot-separated so a reader can group on its hierarchy. A name this build has never seen still
/// renders: nothing here holds a list of the valid ones.
/// </param>
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

    /// <summary>
    /// A rule that decided on its own — the reactor's actor.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Not <see cref="System"/>, and never <see cref="User"/>.</b> Nobody performed it and no
    /// standing configuration produced it: something judged a condition and concluded that this should
    /// happen. A reader has to be able to ask which rule, and reading it as a person would claim
    /// somebody acted at three in the morning. Who wrote the rule is a separate field on the event.
    /// </remarks>
    public const string Rule = "rule";
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

    /// <summary>
    /// A rule the reactor evaluated. The name after it is the rule's id.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Not an identity provider, and that is why it needs naming.</b> Every other value here
    /// answers "how do we know who this is"; this one answers "nobody — a rule concluded it". Left
    /// unrecognised, the prefix would be discarded and the rule id would read as a person's name.
    /// </remarks>
    public const string Rule = "rule";
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

    /// <summary>
    /// The reactor deciding on its own — no surface, and nobody in front of one.
    /// </summary>
    /// <remarks>
    /// Its own value rather than <see cref="System"/>, which is the scheduler and the engine's own
    /// housekeeping: those run because somebody configured a time, where this one runs because a rule
    /// read a condition and concluded something. A reader looking at what happened overnight needs to
    /// tell "the clock came round" from "something judged this worth doing".
    /// </remarks>
    public const string Reactor = "reactor";

    /// <summary>True if <paramref name="origin"/> is one of the closed set (used to normalize an event's
    /// origin; an unrecognized value is treated as null — never fabricated). ⚠ A value stamped on an
    /// engine call but missing here comes back off the echo as <see langword="null"/>: this is the gate
    /// the whole provenance passes through, not a display list.</summary>
    public static bool IsKnown(string? origin) =>
        origin is Ui or Assistant or Discord or System or Api or Notification or Reactor;

    /// <summary>True if a client may declare <paramref name="origin"/> on the command path — everything
    /// except the two this host stamps for itself. A caller naming <see cref="Notification"/> would be
    /// claiming to be a redemption this API performed, which is exactly the claim it cannot check.</summary>
    public static bool IsCallerDeclarable(string origin) =>
        origin is Ui or Assistant or Discord or Api;
}
