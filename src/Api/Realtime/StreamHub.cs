using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

using TheKrystalShip.Api.Contracts;

using TheKrystalShip.KGSM.Auth.Access;
using TheKrystalShip.KGSM.Auth.Cluster;

namespace TheKrystalShip.Api.Realtime;

/// <summary>
/// The per-host connection registry and fan-out point. The <c>StreamController</c> registers/
/// unregisters each live <see cref="StreamConnection"/>; the pumps publish topic messages, which the
/// hub routes only to the connections subscribed to that topic. A message is serialized <em>once</em>
/// per publish and the same bytes are enqueued to every subscriber (no per-connection re-serialization),
/// using the shared HTTP JSON options so the wire shape matches the REST surface exactly.
/// </summary>
public sealed class StreamHub
{
    // Reference-keyed set of live connections. The byte value is unused.
    private readonly ConcurrentDictionary<StreamConnection, byte> _connections = new();
    private readonly JsonSerializerOptions _json;

    public StreamHub(IOptions<JsonOptions> httpJsonOptions)
    {
        _json = httpJsonOptions.Value.SerializerOptions;
    }

    /// <summary>The shared JSON options (camelCase + ISO-8601 'Z').</summary>
    public JsonSerializerOptions Json => _json;

    public void Add(StreamConnection connection) => _connections.TryAdd(connection, 0);
    public void Remove(StreamConnection connection) => _connections.TryRemove(connection, out _);

    /// <summary>True if any live connection is subscribed to <paramref name="topic"/>. Pumps gate work on this.</summary>
    public bool HasSubscribers(string topic)
    {
        foreach (StreamConnection c in _connections.Keys)
            if (c.Receives(topic)) return true;
        return false;
    }

    /// <summary>True if any live connection has a subscription matching <paramref name="match"/> (e.g. any <c>*/metrics</c> topic).</summary>
    public bool AnySubscription(Func<string, bool> match)
    {
        foreach (StreamConnection c in _connections.Keys)
            if (c.HasMatchingSubscription(match)) return true;
        return false;
    }

    /// <summary>
    /// Build an SSE frame: <c>data: &lt;json&gt;\n\n</c> as UTF-8 bytes. Called once per publish
    /// (all connections are SSE now); the same bytes are enqueued to every subscriber.
    /// </summary>
    /// <remarks>
    /// Static and shared, so the one message a connection builds for itself — the <c>me.patch</c> its
    /// own authority re-check produces — is framed by the same code as every fanned-out one. Two
    /// renderings of one wire format is a drift waiting to happen.
    /// </remarks>
    internal static byte[] BuildFrame(StreamMessage message, JsonSerializerOptions json) =>
        Encoding.UTF8.GetBytes("data: " + JsonSerializer.Serialize(message, json) + "\n\n");

    private byte[] BuildSseFrame(StreamMessage message) => BuildFrame(message, _json);

    /// <summary>
    /// Route <paramref name="message"/> to every connection subscribed to <paramref name="topic"/>,
    /// coalescing per <paramref name="coalesceKey"/> within each connection's outbound queue. Serializes
    /// at most once, and only when there is at least one subscriber.
    /// </summary>
    /// <param name="serverId">
    /// The server the frame is about, on a server collection topic (<see cref="TopicGateKind.PerServer"/>):
    /// a reader who may not read that server is not sent it. <see langword="null"/> on any other topic.
    /// </param>
    /// <param name="redacted">
    /// The message to send instead to a reader who lacks the action it names. Null — the usual case —
    /// sends <paramref name="message"/> to everybody who receives the topic. This is how a frame whose
    /// <em>values</em> depend on who is reading reaches both audiences without the topic itself being
    /// restricted: the audit feed says the same things to everyone, and only the values inside a row
    /// differ (<c>AuditRedaction</c>). Access is read per frame, so a reader who loses the action gets
    /// the redacted variant from the next frame on.
    /// </param>
    public void Publish(
        string topic, string coalesceKey, StreamMessage message,
        string? serverId = null, StreamRedaction? redacted = null)
    {
        ReadOnlyMemory<byte>? frame = null;
        ReadOnlyMemory<byte>? restricted = null;

        foreach (StreamConnection c in _connections.Keys)
        {
            if (!c.Receives(topic)) continue;

            if (serverId is not null && !c.Access.Allows(StreamProtocol.ServerReadAction, serverId)) continue;

            if (redacted is not null && !c.Access.Allows(redacted.Action))
            {
                restricted ??= BuildSseFrame(redacted.Message);
                c.Enqueue(coalesceKey, restricted.Value);
                continue;
            }

            frame ??= BuildSseFrame(message);
            c.Enqueue(coalesceKey, frame.Value);
        }
    }

    /// <summary>
    /// Route one frame made of per-server <paramref name="rows"/> to every connection receiving
    /// <paramref name="topic"/>, each cut to the rows whose server its reader may read.
    /// </summary>
    /// <remarks>
    /// Readers who may read the same servers share one serialization: frames are built once per distinct
    /// set, and on a host where everybody reads everything that is once.
    /// </remarks>
    public void PublishRows<T>(
        string topic, string coalesceKey, IReadOnlyList<T> rows, Func<T, string> serverOf,
        Func<IReadOnlyList<T>, StreamMessage> render)
    {
        Dictionary<string, ReadOnlyMemory<byte>>? frames = null;

        foreach (StreamConnection c in _connections.Keys)
        {
            if (!c.Receives(topic)) continue;

            var visible = new List<T>(rows.Count);
            var signature = new StringBuilder(rows.Count);
            foreach (T row in rows)
            {
                bool allowed = c.Access.Allows(StreamProtocol.ServerReadAction, serverOf(row));
                signature.Append(allowed ? '1' : '0');
                if (allowed) visible.Add(row);
            }

            frames ??= new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
            string key = signature.ToString();
            if (!frames.TryGetValue(key, out ReadOnlyMemory<byte> frame))
            {
                frame = BuildSseFrame(render(visible));
                frames[key] = frame;
            }

            c.Enqueue(coalesceKey, frame);
        }
    }

    /// <summary>
    /// Route <paramref name="message"/> only to the live connections authenticated as
    /// <paramref name="accountId"/>, and only those of them subscribed to <paramref name="topic"/>.
    /// </summary>
    /// <remarks>
    /// The audience is the account, not the session: somebody signed in on a laptop and a phone holds
    /// two connections and a fact about their account is true on both. Matching is on the account id
    /// rather than the token's handle, so a session established through a linked provider identity is
    /// reached as readily as one established with a password. A connection that proves no account
    /// here belongs to nobody and is never a recipient — <see cref="StreamConnection.BelongsTo"/>.
    /// </remarks>
    /// <summary>The accounts with at least one live connection subscribed to <paramref name="topic"/>.</summary>
    public IReadOnlyCollection<string> AccountsSubscribedTo(string topic) =>
        [.. _connections.Keys
            .Where(c => c.AccountId is not null && c.IsSubscribed(topic))
            .Select(c => c.AccountId!)
            .Distinct(StringComparer.Ordinal)];

    public void PublishToAccount(string accountId, string topic, string coalesceKey, StreamMessage message)
    {
        ReadOnlyMemory<byte>? frame = null;

        foreach (StreamConnection c in _connections.Keys)
        {
            if (!c.BelongsTo(accountId) || !c.IsSubscribed(topic)) continue;

            frame ??= BuildSseFrame(message);
            c.Enqueue(coalesceKey, frame.Value);
        }
    }

    /// <summary>
    /// This node's replica took a change: every live connection evaluates from
    /// <paramref name="evaluator"/> from its next frame on, and a reader whose account status moved is
    /// told on <c>me</c>.
    /// </summary>
    /// <remarks>
    /// Called once the replica has applied the change, so a change made at the anchor lands on the
    /// affected person's open panel at once. The connection's own re-check is the backstop, and answers
    /// within its own interval.
    /// </remarks>
    public void AccessChanged(AccessEvaluator evaluator)
    {
        foreach (StreamConnection c in _connections.Keys)
            c.AccessChanged(evaluator);
    }
}

/// <summary>The variant of a frame sent to a reader who lacks <paramref name="Action"/>.</summary>
public sealed record StreamRedaction(string Action, StreamMessage Message);

/// <summary>
/// Carries every change this node's replica takes to the open streams (<see cref="StreamHub.AccessChanged"/>).
/// </summary>
public sealed class StreamAccessRefresh(StreamHub hub, MemberAccess access) : IAuthorityChangeListener
{
    /// <inheritdoc />
    public async Task AuthorityChangedAsync(CancellationToken ct)
    {
        if (await access.EvaluatorAsync(ct).ConfigureAwait(false) is { } evaluator)
            hub.AccessChanged(evaluator);
    }
}
