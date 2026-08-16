using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.Api.Services.Leaves;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Events;
using TheKrystalShip.KGSM.Lifecycle;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Reading what each leaf says is broken about itself.
/// </summary>
/// <remarks>
/// ⚠ The half a probe cannot see. <c>/health</c> answers yes or no, so a leaf answering perfectly
/// while unable to do part of its job reads as operational — and the two socket-activated leaves
/// cannot be probed at all, because connecting to the socket is what starts them.
/// </remarks>
public sealed class LeafDegradationTrackerTests : IDisposable
{
    private readonly string _root;

    public LeafDegradationTrackerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "kgsm-api-degradation", Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Already gone.
        }
    }

    [Fact]
    public void A_leaf_that_reported_a_fault_is_read_back_from_its_own_journal()
    {
        Journal("kgsm-monitor", Degraded("net-meter"), Degraded("sampling"));

        LeafDegradationTracker tracker = RunOnce();

        Assert.Equal(["net-meter", "sampling"], tracker.For("monitor").Order());
    }

    [Fact]
    public void A_fault_that_was_recovered_is_not_reported()
    {
        Journal("kgsm-scheduler", Degraded("watchdog"), Recovered("watchdog"));

        LeafDegradationTracker tracker = RunOnce();

        Assert.Empty(tracker.For("scheduler"));
    }

    [Fact]
    public void A_leaf_with_nothing_to_report_is_absent_rather_than_present_and_empty()
    {
        // So a caller cannot mistake "reported nothing" for "reported it is fine". The two are
        // different facts and only one of them is a measurement.
        Journal("kgsm-watchdog", Degraded("cgroup-kill"), Recovered("cgroup-kill"));

        LeafDegradationTracker tracker = RunOnce();

        Assert.DoesNotContain("kgsm-watchdog", tracker.Current.Keys);
    }

    [Fact]
    public void The_engine_is_not_a_leaf_and_its_journal_is_not_read()
    {
        // It reports no lifecycle, and it is much the largest journal on the host — reading it every
        // tick to learn that would be the whole cost of this for none of the value.
        Journal("kgsm", Degraded("something"));

        LeafDegradationTracker tracker = RunOnce();

        Assert.Empty(tracker.Current);
    }

    [Fact]
    public void The_producer_prefix_is_translated_to_the_leaf_id_the_capability_model_uses()
    {
        // ⚠ Two vocabularies: a producer is kgsm-monitor and the capability model calls the same leaf
        // monitor. One place owns the mapping.
        Journal("kgsm-assistant", Degraded("llm-backend"));

        LeafDegradationTracker tracker = RunOnce();

        Assert.Equal(["llm-backend"], tracker.For("assistant"));
        Assert.Contains("kgsm-assistant", tracker.Current.Keys);
    }

    [Fact]
    public void A_leaf_this_host_does_not_have_reports_nothing()
    {
        LeafDegradationTracker tracker = RunOnce();

        Assert.Empty(tracker.For("monitor"));
        Assert.Empty(tracker.Current);
    }

    /// <summary>Takes one reading against the temp state root and returns the tracker.</summary>
    /// <remarks>
    /// The reading directly rather than through the hosted service's start and stop, which would be
    /// testing the host's plumbing rather than what this decides.
    /// </remarks>
    private LeafDegradationTracker RunOnce()
    {
        var tracker = new LeafDegradationTracker(
            new FixedDiscovery(_root), NullLogger<LeafDegradationTracker>.Instance);

        tracker.Refresh();
        return tracker;
    }

    private void Journal(string producer, params string[] lines)
    {
        string directory = JournalLayout.DirectoryFor(producer, _root);
        Directory.CreateDirectory(directory);
        File.WriteAllLines(Path.Combine(directory, "2026-08-16.ndjson"), lines);
    }

    private static string Degraded(string component) => Line(LeafLifecycleEvents.Degraded, component);

    private static string Recovered(string component) => Line(LeafLifecycleEvents.Recovered, component);

    private static string Line(string type, string component) => $$"""
        {"V":1,"EventType":"{{type}}","Data":{"Component":"{{component}}"},"Timestamp":"2026-08-16T10:00:00.000Z"}
        """;

    /// <summary>Discovery over a temp state root, so no test touches this host's real journals.</summary>
    private sealed class FixedDiscovery(string root) : IJournalDiscovery
    {
        public IReadOnlyList<JournalSource> Discover()
        {
            if (!Directory.Exists(root))
                return [];

            return
            [
                .. Directory.GetDirectories(root)
                    .Select(d => new JournalSource(
                        Path.GetFileName(d), Path.Combine(d, JournalLayout.Subdirectory)))
                    .Where(s => Directory.Exists(s.Directory))
                    .OrderBy(s => s.Producer, StringComparer.Ordinal),
            ];
        }
    }
}
