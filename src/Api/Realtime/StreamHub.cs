using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

using TheKrystalShip.Api.Contracts;


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
    /// Static and shared, so a message a connection builds for itself is framed by the same code as
    /// every fanned-out one. Two renderings of one wire format is a drift waiting to happen.
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
    /// Set on a frame about a server (<see cref="TopicGateKind.PerServer"/>): a reader who may not read
    /// servers is not sent it. <see langword="null"/> on any other topic.
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

            if (serverId is not null && !c.Access.Allows(StreamProtocol.ServerReadAction)) continue;

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
    /// <paramref name="topic"/> whose reader may read servers. Every reader that may sees the same rows,
    /// so the frame is serialized once.
    /// </summary>
    public void PublishRows<T>(
        string topic, string coalesceKey, IReadOnlyList<T> rows, Func<IReadOnlyList<T>, StreamMessage> render)
    {
        ReadOnlyMemory<byte>? frame = null;

        foreach (StreamConnection c in _connections.Keys)
        {
            if (!c.Receives(topic) || !c.Access.Allows(StreamProtocol.ServerReadAction)) continue;

            frame ??= BuildSseFrame(render(rows));
            c.Enqueue(coalesceKey, frame.Value);
        }
    }
}

/// <summary>The variant of a frame sent to a reader who lacks <paramref name="Action"/>.</summary>
public sealed record StreamRedaction(string Action, StreamMessage Message);
