using TheKrystalShip.Api.Services.Auth;

namespace TheKrystalShip.Api.Realtime;

/// <summary>
/// What the reader behind one stream may see: the actions their token lists, fixed for as long as the
/// connection lives. The connection ends when the token expires, and the client reconnects with a fresh
/// one, so a role change reaches an open stream at its next renewal.
/// </summary>
/// <remarks>
/// <para>
/// The hub asks it on every frame. A host run with auth switched off, and another member acting as itself,
/// stream to a reader that is <see cref="Unrestricted"/>. A reader whose token lists nothing is
/// <see cref="Nobody"/> and sees only its own <c>me</c> topic.
/// </para>
/// </remarks>
public sealed class StreamAccess
{
    private readonly IReadOnlySet<string>? _held;

    private StreamAccess(string? accountId, IReadOnlySet<string>? held)
    {
        AccountId = accountId;
        _held = held;
    }

    /// <summary>A reader holding every action: the open caller of an auth-disabled host, or a member acting as itself.</summary>
    public static StreamAccess Unrestricted { get; } = new(null, null);

    /// <summary>A reader holding no action: its own <c>me</c> topic and nothing else.</summary>
    public static StreamAccess Nobody { get; } = new(null, new HashSet<string>());

    /// <summary>The reader whose token names <paramref name="accountId"/> and lists <paramref name="held"/>.</summary>
    public static StreamAccess For(string accountId, IReadOnlySet<string> held) => new(accountId, held);

    /// <summary>The person the reader is, or <see langword="null"/> for the two fixed readers.</summary>
    public string? AccountId { get; }

    /// <summary>Whether the reader may perform <paramref name="action"/>.</summary>
    public bool Allows(string action) => _held is null || _held.Contains(action);

    /// <summary>Whether the reader holds every action — what a topic nothing declares needs.</summary>
    public bool HoldsEverything => _held is null;

    /// <summary>Whether the reader may subscribe to <paramref name="topic"/> at all.</summary>
    public bool Reaches(string topic)
    {
        TopicGate gate = StreamProtocol.Gate(topic);
        return gate.Kind switch
        {
            TopicGateKind.Open or TopicGateKind.PerServer => true,
            TopicGateKind.Node or TopicGateKind.Server => Allows(gate.Action!),
            _ => HoldsEverything,
        };
    }
}
