using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Api.Services.Players;

/// <summary>
/// Whether this host can see who is connected to an instance at all — the one place that question is
/// answered, for every surface that asks it (<c>GET /servers/{id}/players</c>'s <c>detection</c> field
/// and the <see cref="Contracts.Server.OnlinePlayers"/> count on every server element).
/// </summary>
/// <remarks>
/// <para><b>The supervisor answers it.</b> Detection is the watchdog's reading
/// (<c>IWatchdogClient.GetPlayerPresenceAsync</c> → <c>WatchdogInstancePresence.IsDetected</c>), because
/// the watchdog is what actually scrapes an instance's log for the join/leave patterns. Nothing here
/// re-derives it from a blueprint.</para>
/// <para><b>Not knowable is not false.</b> No watchdog, an unreachable one, or an instance the map does
/// not mention all read as <em>unobservable</em> — the same answer a game with no detection configured
/// gets. That is deliberately conservative: every caller turns unobservable into an honest unknown
/// ("presence not available"), never into "nobody is here".</para>
/// <para><b>Cached, because the answer barely moves.</b> Detection changes when an instance's
/// configuration does, not when someone joins — so a reading is reused for <see cref="Ttl"/> and the
/// per-second server roster build costs no socket round trip. One refresh runs at a time; the losers of
/// the race read the reading it lands.</para>
/// </remarks>
public sealed class PlayerObservability(IServiceProvider services, ILogger<PlayerObservability> logger)
{
    /// <summary>How long a reading is reused. Short enough that an instance reconfigured (or a watchdog
    /// restarted) is noticed within a few roster ticks, long enough that the 5s domain pump and a burst
    /// of REST reads share one socket call.</summary>
    internal static readonly TimeSpan Ttl = TimeSpan.FromSeconds(15);

    /// <summary>How long the supervisor gets to answer before the reading is abandoned. A presence query
    /// is a local socket read; anything slower than this is a watchdog in trouble, and waiting on it
    /// would stall a roster build.</summary>
    private static readonly TimeSpan Probe = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _gate = new(1, 1);

    // The last successful reading, or null when none has landed (or the last attempt failed). Replaced
    // whole, never mutated, so a reader always sees one consistent map.
    private volatile IReadOnlyDictionary<string, bool>? _detected;
    private long _readAtTicks;

    /// <summary>
    /// Whether presence on this instance is observable, from the last reading. Synchronous and
    /// allocation-free — this is what a roster build calls per server. <see langword="false"/> when the
    /// reading says the instance is not detected, when the instance is absent from it, and when there is
    /// no reading at all.
    /// </summary>
    public bool IsObservable(string id)
    {
        IReadOnlyDictionary<string, bool>? map = _detected;
        return map is not null && !string.IsNullOrEmpty(id)
            && map.TryGetValue(id, out bool detected) && detected;
    }

    /// <summary>
    /// Take a fresh reading if the held one has aged past <see cref="Ttl"/>, then leave it for
    /// <see cref="IsObservable"/>. Never throws: a failure leaves the map cleared, which reads as
    /// unobservable everywhere — the honest answer when the supervisor cannot be asked.
    /// </summary>
    public async Task RefreshIfStaleAsync(CancellationToken ct)
    {
        if (!IsStale()) return;

        try { await _gate.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        try
        {
            // A refresh that landed while this caller queued is this caller's refresh.
            if (!IsStale()) return;

            IReadOnlyDictionary<string, WatchdogInstancePresence>? presence = null;
            if (services.GetService(typeof(IWatchdogClient)) is IWatchdogClient watchdog)
            {
                using var probe = new CancellationTokenSource(Probe);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, probe.Token);
                presence = await watchdog.GetPlayerPresenceAsync(linked.Token).ConfigureAwait(false);
            }

            if (presence is null)
            {
                _detected = null;
            }
            else
            {
                var map = new Dictionary<string, bool>(presence.Count, StringComparer.Ordinal);
                foreach ((string id, WatchdogInstancePresence entry) in presence)
                    map[id] = entry.IsDetected;
                _detected = map;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "player observability: supervisor presence query failed; presence reads as unobservable until the next reading");
            _detected = null;
        }
        finally
        {
            Interlocked.Exchange(ref _readAtTicks, DateTimeOffset.UtcNow.UtcTicks);
            _gate.Release();
        }
    }

    private bool IsStale() =>
        DateTimeOffset.UtcNow.UtcTicks - Interlocked.Read(ref _readAtTicks) > Ttl.Ticks;
}
