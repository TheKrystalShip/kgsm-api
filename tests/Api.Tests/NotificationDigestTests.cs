using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Services.Integrations;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// What a summary says it contains.
/// <para>
/// The headline is the line a lot of people stop reading at, so the thing worth pinning is that it never
/// claims more than the batch supports — a specific phrase only where it is true of every event in it.
/// </para>
/// </summary>
public sealed class NotificationDigestTests
{
    private static NotificationEvent Event(string catalogId, string? serverId = null) =>
        new(catalogId, "irrelevant", serverId, AuditSeverity.Info, $"something about {serverId ?? "the host"}",
            DateTimeOffset.UtcNow, "evt_1");

    [Fact]
    public void A_uniform_batch_says_what_it_is()
    {
        Assert.Equal("3 servers have an update", NotificationDigest.Headline([
            Event("update_available", "a"), Event("update_available", "b"), Event("update_available", "c")]));

        Assert.Equal("2 crashes", NotificationDigest.Headline([Event("crash", "a"), Event("crash", "b")]));
    }

    [Fact]
    public void One_of_something_reads_as_one_of_something()
    {
        Assert.Equal("1 server has an update", NotificationDigest.Headline([Event("update_available", "a")]));
        Assert.Equal("1 crash", NotificationDigest.Headline([Event("crash", "a")]));
    }

    [Fact]
    public void A_mixed_batch_gets_the_flat_count()
    {
        // A headline naming one kind of event over a body listing four others misleads on the surface
        // where a lot of people stop reading.
        Assert.Equal("3 things happened while you were away", NotificationDigest.Headline([
            Event("crash", "a"), Event("update_available", "b"), Event("backup", "c")]));
    }

    [Fact]
    public void An_event_with_no_phrase_of_its_own_still_gets_an_honest_one() =>
        Assert.Equal("2 things happened while you were away",
            NotificationDigest.Headline([Event("something_new"), Event("something_new")]));
}
