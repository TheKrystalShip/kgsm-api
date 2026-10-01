using System.Collections.Concurrent;

using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.KGSM.Auth.Access;
using TheKrystalShip.KGSM.Auth.Users;

namespace TheKrystalShip.Api.Realtime;

/// <summary>
/// What the reader behind one stream may see, evaluated from this node's replica as it stands.
/// </summary>
/// <remarks>
/// <para>
/// The hub asks it on every frame, so a change to the reader's access applies to the next frame after the
/// replica takes it — both ways: a revoked grant stops a topic, a new one starts it, on the connection
/// already open. Answers are remembered per evaluator and forgotten whenever a new one is set, which
/// happens on every change the replica takes and on the connection's own re-check.
/// </para>
/// <para>
/// A host run with auth switched off streams to its synthetic Owner, which is <see cref="Unrestricted"/>.
/// A connection that proves no account on this node is <see cref="Nobody"/>, and sees only its own
/// <c>me</c> topic.
/// </para>
/// </remarks>
public sealed class StreamAccess
{
    private readonly Func<string?, AccessScope>? _targetOf;
    private readonly bool _unrestricted;
    private AccessEvaluator? _evaluator;
    private readonly ConcurrentDictionary<(string Action, string? ServerId), bool> _answers = new();

    private StreamAccess(string? accountId, Func<string?, AccessScope>? targetOf, AccessEvaluator? evaluator, bool unrestricted)
    {
        AccountId = accountId;
        _targetOf = targetOf;
        _evaluator = evaluator;
        _unrestricted = unrestricted;
    }

    /// <summary>The synthetic Owner of an auth-disabled host: every topic, every frame.</summary>
    public static StreamAccess Unrestricted { get; } = new(null, null, null, unrestricted: true);

    /// <summary>A reader holding no account here: its own <c>me</c> topic and nothing else.</summary>
    public static StreamAccess Nobody { get; } = new(null, null, null, unrestricted: false);

    /// <summary>The reader behind <paramref name="accountId"/> on this node, evaluated by <paramref name="evaluator"/>.</summary>
    public static StreamAccess For(string accountId, NodeAccess node, AccessEvaluator evaluator) =>
        For(accountId, evaluator, serverId => serverId is null ? node.NodeTarget : node.ServerTarget(serverId));

    /// <summary>
    /// The reader behind <paramref name="accountId"/>, evaluated by <paramref name="evaluator"/> at the
    /// targets <paramref name="targetOf"/> names: a server by its id, this node for <see langword="null"/>.
    /// </summary>
    public static StreamAccess For(string accountId, AccessEvaluator evaluator, Func<string?, AccessScope> targetOf) =>
        new(accountId, targetOf, evaluator, unrestricted: false);

    /// <summary>The account the reader is, or <see langword="null"/> for the two fixed readers.</summary>
    public string? AccountId { get; }

    /// <summary>Whether this reader is evaluated from the replica, and so has an evaluator to replace.</summary>
    public bool Evaluated => AccountId is not null && _targetOf is not null;

    /// <summary>The status reported for an identity this node holds no account for, or cannot read.</summary>
    public const string UnknownStatus = "unknown";

    /// <summary>
    /// The account's status in the evaluator's snapshot, in the wire vocabulary: <see cref="UnknownStatus"/>
    /// once the account is gone from it, and <see langword="null"/> for the two fixed readers.
    /// </summary>
    public string? Status
    {
        get
        {
            if (!Evaluated || Volatile.Read(ref _evaluator) is not { } evaluator)
                return null;

            return evaluator.Snapshot.Accounts.TryGetValue(AccountId!, out AccessAccount? account)
                ? StatusWire(account.Status)
                : UnknownStatus;
        }
    }

    /// <summary>An account status in the wire vocabulary <c>GET /me</c> answers in.</summary>
    public static string StatusWire(AccountStatus status) => status switch
    {
        AccountStatus.Active => UserStatuses.Active,
        AccountStatus.Disabled => UserStatuses.Disabled,
        _ => UserStatuses.Pending,
    };

    /// <summary>Answer from <paramref name="evaluator"/> from now on.</summary>
    public void Update(AccessEvaluator evaluator)
    {
        if (!Evaluated)
            return;

        Volatile.Write(ref _evaluator, evaluator);
        _answers.Clear();
    }

    /// <summary>
    /// Whether the reader may perform <paramref name="action"/> at the server
    /// <paramref name="serverId"/>, or at this node when it is <see langword="null"/>.
    /// </summary>
    public bool Allows(string action, string? serverId = null)
    {
        if (_unrestricted)
            return true;

        if (!Evaluated || Volatile.Read(ref _evaluator) is not { } evaluator)
            return false;

        return _answers.GetOrAdd((action, serverId), key =>
            evaluator.Allows(AccountId!, key.Action, _targetOf!(key.ServerId)).Allowed);
    }

    /// <summary>Whether the reader holds every action everywhere — what a topic nothing declares needs.</summary>
    public bool HoldsEverything =>
        _unrestricted
        || (Evaluated && Volatile.Read(ref _evaluator) is { } evaluator && evaluator.HoldsEverything(AccountId!));

    /// <summary>Whether the reader may subscribe to <paramref name="topic"/> at all.</summary>
    public bool Reaches(string topic)
    {
        TopicGate gate = StreamProtocol.Gate(topic);
        return gate.Kind switch
        {
            TopicGateKind.Open or TopicGateKind.PerServer => true,
            TopicGateKind.Node => Allows(gate.Action!),
            TopicGateKind.Server => Allows(gate.Action!, gate.ServerId),
            _ => HoldsEverything,
        };
    }
}
