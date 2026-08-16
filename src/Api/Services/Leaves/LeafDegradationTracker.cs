using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Events;
using TheKrystalShip.KGSM.Lifecycle;

namespace TheKrystalShip.Api.Services.Leaves;

/// <summary>
/// What each leaf on this host says is broken about itself.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>The half a probe cannot see.</b> <see cref="LeafHealthMonitor"/> asks each leaf's
/// <c>/health</c> and gets a yes or a no, so a leaf that is answering perfectly while unable to do
/// part of its job reads as operational — an assistant with a dead backend, a monitor serving a frozen
/// frame, a scheduler that cannot reach the watchdog. And the two socket-activated leaves cannot be
/// probed at all, because connecting to the socket is what starts them.
/// </para>
/// <para>
/// <b>Read from each producer's own journal, not from the event stream.</b> A lifecycle payload
/// deliberately names no leaf — the producer comes from the directory a line was read out of, which a
/// reader can check where a field inside the payload would be a claim it cannot. A live handler is
/// handed the payload alone and could only guess from the actor, so this reads where the answer
/// actually is.
/// </para>
/// <para>
/// <b>Nothing here decides what a degradation means.</b> It reports which components a leaf named and
/// what it said about them; whether that makes a capability degraded is the capability model's
/// business, and what to tell somebody is a surface's.
/// </para>
/// </remarks>
public sealed class LeafDegradationTracker(
    IJournalDiscovery discovery,
    ILogger<LeafDegradationTracker> logger) : BackgroundService
{
    /// <summary>
    /// How often each journal is re-read.
    /// </summary>
    /// <remarks>
    /// Slower than the capability poll on purpose. These are conditions that persist rather than
    /// spikes, and the steady-state cost is one <c>stat</c> per journal — a segment is only re-read
    /// when its modification time has moved.
    /// </remarks>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    private readonly Dictionary<string, DateTime> _seen = new(StringComparer.Ordinal);

    private volatile IReadOnlyDictionary<string, IReadOnlyCollection<string>> _current =
        new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal);

    /// <summary>
    /// The components each producer currently reports broken, keyed by producer id.
    /// </summary>
    /// <remarks>
    /// A producer with nothing broken is absent from the map rather than present with an empty list,
    /// so a caller cannot mistake "reported nothing" for "reported it is fine".
    /// </remarks>
    public IReadOnlyDictionary<string, IReadOnlyCollection<string>> Current => _current;

    /// <summary>
    /// What <paramref name="leaf"/> reports broken, by the Control Panel's unprefixed leaf id.
    /// </summary>
    /// <remarks>
    /// ⚠ The two vocabularies differ: a producer is <c>kgsm-monitor</c> and the capability model calls
    /// the same leaf <c>monitor</c>. Translated here rather than at each call site, so one place owns
    /// the mapping.
    /// </remarks>
    /// <param name="leaf">The leaf id, unprefixed (<c>monitor</c>, <c>watchdog</c>, …).</param>
    /// <returns>The degraded component ids, empty when it reports none.</returns>
    public IReadOnlyCollection<string> For(string leaf) =>
        _current.TryGetValue(JournalProducer.EcosystemPrefix + leaf, out IReadOnlyCollection<string>? c)
            ? c
            : [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        try
        {
            do
            {
                Refresh();
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    /// <summary>
    /// Takes one reading of every leaf's journal.
    /// </summary>
    /// <remarks>
    /// Internal so a test can take a reading without driving a hosted service's start and stop, which
    /// would be testing the host rather than what this decides.
    /// </remarks>
    internal void Refresh()
    {
        var next = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal);
        bool changed = false;

        try
        {
            foreach (JournalSource source in discovery.Discover())
            {
                // The engine is not a leaf and reports no lifecycle. Reading its journal — much the
                // largest on the host — every tick to learn that would be the whole cost of this for
                // none of the value.
                if (source.Producer == JournalProducer.Kgsm)
                    continue;

                IReadOnlyCollection<string> degraded = LeafState.DegradedComponents(source.Directory);

                if (degraded.Count > 0)
                    next[source.Producer] = degraded;

                if (Moved(source))
                    changed = true;
            }
        }
        catch (Exception ex)
        {
            // A read that failed is a reading not taken, which the next tick retries. Reporting every
            // leaf healthy because the scan threw would be the fabricated status this whole layer is
            // meant to remove.
            logger.LogDebug(ex, "could not read what the leaves say about themselves");
            return;
        }

        _current = next;

        if (changed)
            logger.LogDebug("leaf self-reports changed: {Count} leaf/leaves report a fault", next.Count);
    }

    /// <summary>Whether this producer's newest segment has been written since the last look.</summary>
    /// <remarks>
    /// Only used to decide whether the change is worth a log line. The read itself is cheap enough to
    /// do unconditionally, and skipping it on an unchanged mtime would miss a segment rolling over at
    /// midnight into a file whose first write lands in the same second.
    /// </remarks>
    private bool Moved(JournalSource source)
    {
        try
        {
            string[] segments = Directory.GetFiles(source.Directory, "*.ndjson");

            if (segments.Length == 0)
                return false;

            Array.Sort(segments, StringComparer.Ordinal);
            DateTime written = File.GetLastWriteTimeUtc(segments[^1]);

            if (_seen.TryGetValue(source.Producer, out DateTime last) && last == written)
                return false;

            _seen[source.Producer] = written;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
