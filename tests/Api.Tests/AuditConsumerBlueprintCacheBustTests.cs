using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using TheKrystalShip.Api.Services.Library;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Events;

using Xunit;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Closes Phase 6 of <c>blueprint-editor-plan.md</c>: the <see cref="BlueprintCache"/> is bust-driven
/// by the audit consumer's blueprint-event handlers (Phase 4 §4.5 — the wiring lives at
/// <c>KgsmAuditConsumer.RegisterHandlers</c>), so an engineer-originated or assistant-originated
/// blueprint edit refreshes the catalog the engine serves on the next read. <see cref="KgsmAuditConsumer"/>
/// is a singleton hosted service — its <see cref="KgsmAuditConsumer.StartAsync"/> runs once at host
/// build time, which is where <c>events.RegisterHandler&lt;T&gt;</c> wires the typed handlers. This test
/// captures that registration through the host's DI-graph (the fake <see cref="IEventService"/>),
/// drives dispatch by hand with a <see cref="BlueprintUpdatedData"/>, and asserts the cache's catalog
/// flips to the new shape — direct structural proof the bust fires, independent of any HTTP request.
/// </summary>
/// <remarks>
/// <b>Substitute-only at the engine chokepoint.</b> The fake <see cref="IEventService"/> never binds a
/// real socket and is the only engine façade this test reaches — the audit consumer's
/// <c>events.Initialize()</c> call is a silent no-op on it, and the cache itself is the REAL
/// <see cref="BlueprintCache"/>: substituting it would defeat the purpose (the test needs to verify its
/// state mutates as the bust demands, not its call count). <see cref="IBlueprintService"/> is a
/// hand-rolled fake whose <c>ListDetailed()</c> returns the live catalog by reference, so swapping the
/// catalog at runtime under the cache's nose is observable on the next refresh without re-registering.
/// The Api.Tests project ships with only xUnit + WebApplicationFactory (no NSubstitute/Moq), so the
/// fakes are minimal hand-rolled — <see cref="RecordingEventService"/>, <see cref="MutableBlueprintService"/>.
/// </remarks>
public sealed class AuditConsumerBlueprintCacheBustTests : IClassFixture<AuditConsumerBlueprintCacheBustTests.BustFactory>
{
    private readonly BustFactory _factory;

    public AuditConsumerBlueprintCacheBustTests(BustFactory factory) => _factory = factory;

    [Fact]
    public async Task BlueprintUpdatedEvent_DispatchedThroughAuditConsumer_BustsTheBlueprintCache()
    {
        // Resolve the real cache (singleton) — populated at host build from the factory's initial
        // catalog A (a single `factorio` blueprint). TTL defaults to 60 s, so within the test window the
        // background timer never fires an unsolicited refresh — any cache mutation in flight IS the bust.
        BlueprintCache cache = _factory.Services.GetRequiredService<BlueprintCache>();
        IReadOnlyDictionary<string, Blueprint> initial = cache.GetAll();
        Assert.True(initial.ContainsKey(BustFactory.BlueprintA), "cache should be primed with catalog A at startup");
        Assert.False(initial.ContainsKey(BustFactory.BlueprintB));

        // Switch the IBlueprintService fake's catalog to B. Without a bust the cache stays stale — the
        // proof the eventual refresh was driven by the event, not a background tick.
        _factory.SwitchCatalogToB();

        // Drain the timer once: if a TTL tick were lurking (it shouldn't be at 60s) the test would fail
        // here, exposing a TTL that's too short rather than a missing bust. Keep the wait small so a real
        // failure mode surfaces fast.
        await Task.Delay(50);
        IReadOnlyDictionary<string, Blueprint> stillStale = cache.GetAll();
        Assert.True(stillStale.ContainsKey(BustFactory.BlueprintA),
            "cache must not refresh on its own within the test window — TTL too short?");
        Assert.False(stillStale.ContainsKey(BustFactory.BlueprintB));

        // The load-bearing structural assertion: a blueprint_updated envelope dispatched through the
        // typed handler KgsmAuditConsumer.RegisterHandlers(events) registered for BlueprintUpdatedData
        // calls blueprintCache.TryRefresh() (KgsmAuditConsumer.cs:299-316). The captured handler is the
        // same lambda the live audit consumer would invoke — there is no faking of the consumer; the
        // dispatch path is the production one.
        Assert.NotNull(_factory.Events.CapturedUpdatedHandler);
        await _factory.Events.CapturedUpdatedHandler!.Invoke(new BlueprintUpdatedData
        {
            BlueprintName = BustFactory.BlueprintA,
            Tier = BlueprintTier.User,
            OverridesSystem = true,
            Runtime = "native",
        });

        // TryRefresh's refresh path is fire-and-forget (Task.Run + SemaphoreSlim, BlueprintCache.cs:58-69)
        // — the handler returns before the new catalog is in memory. Poll for it: 2 s is generous for a
        // synchronous fake IBlueprintService with zero I/O, well below any TTL or test timeout.
        IReadOnlyDictionary<string, Blueprint> refreshed = stillStale;
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            refreshed = cache.GetAll();
            if (refreshed.ContainsKey(BustFactory.BlueprintB)) break;
            await Task.Delay(25);
        }

        Assert.True(refreshed.ContainsKey(BustFactory.BlueprintB),
            "blueprint_updated event dispatched through KgsmAuditConsumer did NOT bust the BlueprintCache");
        Assert.False(refreshed.ContainsKey(BustFactory.BlueprintA),
            "cache after bust should reflect catalog B exclusively, not a union — the prior contents were replaced, not appended");
    }

    /// <summary>An <see cref="AuthTestFactory"/>-derived host where the engine is provisioned (so
    /// <c>KgsmAuditConsumer</c>'s <c>StartAsync</c> actually wires its typed handlers), the
    /// <see cref="IEventService"/> is a recording fake that captures each
    /// <c>RegisterHandler&lt;T&gt;</c> call into a public field, and the
    /// <see cref="IBlueprintService"/> is a mutable fake whose <c>ListDetailed()</c> reads the live
    /// catalog so swaps are observable on the next call.</summary>
    public sealed class BustFactory : AuthTestFactory
    {
        public const string BlueprintA = "factorio";
        public const string BlueprintB = "terraria";

        private static readonly Blueprint BpA = new() { Name = BlueprintA };
        private static readonly Blueprint BpB = new() { Name = BlueprintB };

        // The live catalog the IBlueprintService fake serves on the next ListDetailed() call. Catalog A
        // at startup; SwitchCatalogToB flips it mid-test to give the bust something to actually refresh
        // — without a swap, TryRefresh firing would be silent (same contents after).
        private Dictionary<string, Blueprint> _currentCatalog = new() { [BlueprintA] = BpA };

        // Captured by RecordingEventService during KgsmAuditConsumer.StartAsync. Set once (the consumer
        // is a singleton hosted service; its StartAsync runs exactly once at host build). A second
        // dispatch test reusing the fixture would still see the same handlers — the registration
        // doesn't expire.
        public RecordingEventService Events { get; } = new();
        public MutableBlueprintService Blueprints { get; }

        public BustFactory()
        {
            // Wire the mutable catalog into both sides before the host builds — ListDetailed() will
            // read it via the same reference the test swaps through SwitchCatalogToB.
            Blueprints = new MutableBlueprintService(_currentCatalog);
        }

        public void SwitchCatalogToB() => _currentCatalog.ClearAdd([BpB]);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // KgsmProvisioned=true ⇒ Startup.cs runs services.AddKgsmServices(...) and registers
                // IEventService/IBlueprintService as singletons — both replaced below.
                ["KGSM_API_KGSM_PATH"] = "/usr/bin/kgsm",
                // Unique socket path: Initialize() on the fake is a no-op and the path never binds,
                // but the consumer logs it, so it must be a per-fixture unique path so parallel test
                // classes don't share a real socket resource by accident.
                ["KGSM_API_KGSM_JOURNAL"] = Path.Combine(Path.GetTempPath(), $"kgsm-api-tests-journal-{Guid.NewGuid():N}"),
            }));

            builder.ConfigureTestServices(services =>
            {
                // The fake IEventService never binds a socket but records every RegisterHandler<T> /
                // RegisterRawHandler call into public fields the test reads. KgsmAuditConsumer calls
                // RegisterHandler (and a single RegisterRawHandler) inside RegisterHandlers → StartAsync.
                services.RemoveAll<IEventService>();
                services.AddSingleton<IEventService>(Events);

                // The IBlueprintService fake is queried both at BlueprintCache startup (initial prime)
                // and on every bust. It returns a fresh clone of the current catalog at call time, so
                // swapping the catalog mid-test is visible to the cache without re-binding the fake.
                services.RemoveAll<IBlueprintService>();
                services.AddSingleton<IBlueprintService>(Blueprints);
            });
        }
    }

    /// <summary>Hand-rolled recording fake for <see cref="IEventService"/> — the Api.Tests project ships
    /// with only xUnit + WebApplicationFactory (no NSubstitute), so the substitute is minimal: every
    /// <c>RegisterHandler&lt;T&gt;</c> call drops its callback into the matching public field, ready for
    /// the test to dispatch. <see cref="Initialize"/> is a deliberate no-op — the real kgsm socket is
    /// not bound; the audit consumer's <c>Initialize()</c> call returns harmlessly so <c>StartAsync</c>
    /// completes its handler-registration path without external I/O.</summary>
    public sealed class RecordingEventService : IEventService
    {
        public Func<BlueprintCreatedData, Task>? CapturedCreatedHandler { get; private set; }
        public Func<BlueprintUpdatedData, Task>? CapturedUpdatedHandler { get; private set; }
        public Func<BlueprintRemovedData, Task>? CapturedRemovedHandler { get; private set; }

        public void Initialize() { /* no transport started — the audit consumer's typing path stays owned by it */ }

        public void Initialize(EventStartPosition startPosition) { /* as above; the position is irrelevant with no journal behind it */ }

        public void RegisterGapHandler(Func<EventJournalGap, Task> handler) { /* the API records no history, so it registers none */ }

        public void RegisterHandler<T>(Func<T, Task> handler) where T : KgsmEventDataBase
        {
            // Switch on the closed-generic types the audit consumer actually registers for blueprints —
            // the three Phase-2 events. Other typed handlers (instance_lifecycle etc.) are also routed
            // through here; we capture only the blueprint ones and ignore the rest, mirroring what the
            // test asserts.
            if (typeof(T) == typeof(BlueprintCreatedData))
                CapturedCreatedHandler = (Func<BlueprintCreatedData, Task>)(object)handler;
            else if (typeof(T) == typeof(BlueprintUpdatedData))
                CapturedUpdatedHandler = (Func<BlueprintUpdatedData, Task>)(object)handler;
            else if (typeof(T) == typeof(BlueprintRemovedData))
                CapturedRemovedHandler = (Func<BlueprintRemovedData, Task>)(object)handler;
            // Other event types are unused here — the consumer's other RegisterHandler<T> calls land
            // on this no-op branch, which is what we want (no recording, no spurious dispatch).
        }

        public void RegisterRawHandler(Func<EventWrapper, Task> handler) { /* the audit consumer's idTracker hook — unused by this test */ }

        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Hand-rolled mutable fake for <see cref="IBlueprintService"/>. Holds the live catalog by
    /// reference (the factory's own field); each <see cref="ListDetailed"/> call returns a fresh clone
    /// so the cache's <c>_cached</c> snapshot is independent of subsequent swaps. Only
    /// <see cref="ListDetailed"/> is reached by <see cref="BlueprintCache.RefreshAsync"/> and is
    /// implemented; the other members throw <see cref="NotImplementedException"/> precisely because the
    /// cache never calls them (any call hitting them surfaces a real but unrelated bug, not a silent
    /// regression).</summary>
    public sealed class MutableBlueprintService : IBlueprintService
    {
        private readonly Dictionary<string, Blueprint> _liveCatalog;

        public MutableBlueprintService(Dictionary<string, Blueprint> liveCatalog) => _liveCatalog = liveCatalog;

        public Dictionary<string, Blueprint> ListDetailed() => new(_liveCatalog);

        // --- Members BlueprintCache never reaches — honest NotImplementedException for an unrelated call,
        // surfacing a real bug (the cache suddenly reaching one of these) rather than a silent regression.
        public List<string> List() => throw new NotImplementedException();
        public List<string> ListDefault() => throw new NotImplementedException();
        public List<string> ListCustom() => throw new NotImplementedException();
        public Blueprint? GetInfo(string name) => throw new NotImplementedException();
        public string? FindPath(string name) => throw new NotImplementedException();
        public BlueprintCandidates? FindAll(string name) => throw new NotImplementedException();
        public BlueprintValidation? Validate(string path) => throw new NotImplementedException();

        public string? GetScaffold() => throw new NotImplementedException();
    }
}

/// <summary>One-shot dictionary swap helper — clears and re-adds so the existing reference held by
/// <see cref="AuditConsumerBlueprintCacheBustTests.MutableBlueprintService"/> reflects the new contents
/// on the next <see cref="IBlueprintService.ListDetailed"/> call (rather than replacing the reference,
/// which would leave the fake pointing at the old dict).</summary>
internal static class DictionarySwapExtensions
{
    public static void ClearAdd<TKey, TValue>(this Dictionary<TKey, TValue> dict, IReadOnlyCollection<TValue> values)
        where TKey : notnull
        where TValue : class
    {
        dict.Clear();
        foreach (var v in values)
        {
            // `Name` is the key the catalog uses (BlueprintCache stores `catalog = ListDetailed()) where
            // ListDetailed returns name-keyed) — this tiny test helper physically belongs to the test
            // arrangement and is the only place that needs the Blueprint → Name pivot.
            var key = (TKey)(object)v.GetType().GetProperty("Name")!.GetValue(v)!;
            dict[key] = v;
        }
    }
}