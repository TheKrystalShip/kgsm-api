using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace TheKrystalShip.Api.Realtime;

/// <summary>
/// The per-host connection registry and fan-out point (M2). The <c>StreamController</c> registers/
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

    /// <summary>The shared JSON options (camelCase + ISO-8601 'Z'); also used by a connection to parse client commands.</summary>
    public JsonSerializerOptions Json => _json;

    public void Add(StreamConnection connection) => _connections.TryAdd(connection, 0);
    public void Remove(StreamConnection connection) => _connections.TryRemove(connection, out _);

    /// <summary>True if any live connection is subscribed to <paramref name="topic"/>. Pumps gate work on this.</summary>
    public bool HasSubscribers(string topic)
    {
        foreach (StreamConnection c in _connections.Keys)
            if (c.IsSubscribed(topic)) return true;
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
    /// Route <paramref name="message"/> to every connection subscribed to <paramref name="topic"/>,
    /// coalescing per <paramref name="coalesceKey"/> within each connection's outbound queue. Serializes
    /// at most once, and only when there is at least one subscriber.
    /// </summary>
    public void Publish(string topic, string coalesceKey, StreamMessage message)
    {
        ReadOnlyMemory<byte>? frame = null;
        foreach (StreamConnection c in _connections.Keys)
        {
            if (!c.IsSubscribed(topic)) continue;
            frame ??= JsonSerializer.SerializeToUtf8Bytes(message, _json);
            c.Enqueue(coalesceKey, frame.Value);
        }
    }
}
