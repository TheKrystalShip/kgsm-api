using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

using TheKrystalShip.Api.Contracts;

using TheKrystalShip.KGSM.Auth;

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
    /// <param name="belowOperator">
    /// The message to send instead to a connection held by a reader below operator. Null — the usual
    /// case — sends <paramref name="message"/> to everybody. This is how a frame whose <em>values</em>
    /// depend on who is reading reaches both audiences without the topic itself being restricted: the
    /// audit feed says the same things to everyone, and only the values inside a row differ
    /// (<c>AuditRedaction</c>). The tier read here is the connection's live one, so a reader demoted
    /// mid-stream starts getting the redacted variant on the next frame rather than on their next
    /// reconnect (<see cref="AuthorityChanged"/>).
    /// </param>
    public void Publish(
        string topic, string coalesceKey, StreamMessage message, StreamMessage? belowOperator = null)
    {
        ReadOnlyMemory<byte>? frame = null;
        ReadOnlyMemory<byte>? restricted = null;

        foreach (StreamConnection c in _connections.Keys)
        {
            if (!c.IsSubscribed(topic)) continue;

            if (belowOperator is not null && !c.IsOperator)
            {
                restricted ??= BuildSseFrame(belowOperator);
                c.Enqueue(coalesceKey, restricted.Value);
                continue;
            }

            frame ??= BuildSseFrame(message);
            c.Enqueue(coalesceKey, frame.Value);
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
    /// An account's standing on this host changed: re-gate every live connection it holds, and tell
    /// each of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two effects, and the order between them is the point. Re-gating comes first and is
    /// unconditional: a demoted reader stops receiving what their new tier does not reach on this
    /// connection, without waiting for a reconnect they may never make. The frame is the courtesy
    /// afterwards, and it goes out whether or not the tier moved, because an approval that leaves the
    /// tier at <c>none</c> and a status changing under an unchanged tier are both news to the panel
    /// showing it.
    /// </para>
    /// <para>
    /// Every write that changes what an account may do calls this. The connection's own re-check is
    /// the backstop for the writers this process never sees — the account store is a shared host file
    /// — and answers within its own interval; this is what makes an admin's change in the Users tab
    /// land on the affected person's open panel at once.
    /// </para>
    /// </remarks>
    public void AuthorityChanged(string accountId, KgsmTier tier, string status)
    {
        foreach (StreamConnection c in _connections.Keys)
        {
            if (c.BelongsTo(accountId))
                c.ApplyTier(tier);
        }

        PublishToAccount(
            accountId,
            StreamProtocol.MeTopic,
            StreamProtocol.MeEntityKey,
            new StreamMessage(
                StreamProtocol.MeTopic,
                StreamProtocol.MePatch,
                new MeStanding(KgsmTiers.ToWire(tier), status)));
    }
}
