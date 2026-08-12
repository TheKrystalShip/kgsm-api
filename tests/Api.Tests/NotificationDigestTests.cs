using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Services.Integrations;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// What a summary says it contains, and what a summary is allowed to offer to do about it.
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
        Assert.Equal("1 person waiting to be let in", NotificationDigest.Headline([Event("awaiting_approval")]));
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

    [Fact]
    public void Update_all_is_offered_only_when_the_whole_batch_is_updates()
    {
        Assert.Equal(["a", "b"], NotificationDigest.UpdatableServers([
            Event("update_available", "a"), Event("update_available", "b")]));

        // One crash in the batch and the button is gone: a batch verb over a mixed list asks for a tap on
        // an instruction whose scope the person cannot read.
        Assert.Empty(NotificationDigest.UpdatableServers([
            Event("update_available", "a"), Event("crash", "b")]));
    }

    [Fact]
    public void The_same_server_twice_is_one_server()
    {
        // A batch that sat long enough can hold two facts about one server, and a button offering to
        // update three things that are two things would be lying about its own scope.
        Assert.Equal(["a", "b"], NotificationDigest.UpdatableServers([
            Event("update_available", "a"), Event("update_available", "b"), Event("update_available", "a")]));
    }

    [Fact]
    public void An_update_naming_no_server_offers_nothing() =>
        Assert.Empty(NotificationDigest.UpdatableServers([
            Event("update_available", "a"), Event("update_available", null)]));
}

/// <summary>
/// How the one multi-target action carries its targets — the single place in this design where a
/// separator is used rather than a column.
/// </summary>
public sealed class PushActionTargetsTests
{
    [Fact]
    public void A_list_of_server_ids_round_trips()
    {
        string joined = PushActionTargets.Join(["factorio-01", "romestead", "minecraft"]);
        Assert.Equal(["factorio-01", "romestead", "minecraft"], PushActionTargets.Split(joined));
    }

    [Fact]
    public void Nothing_reads_as_nothing_rather_than_as_one_empty_target()
    {
        Assert.Empty(PushActionTargets.Split(null));
        Assert.Empty(PushActionTargets.Split(""));
        Assert.Empty(PushActionTargets.Split("   "));
        Assert.Empty(PushActionTargets.Split(",,,"));
    }

    [Fact]
    public void Stray_whitespace_does_not_become_part_of_a_server_name() =>
        Assert.Equal(["a", "b"], PushActionTargets.Split("a, b"));

    [Fact]
    public void The_batch_target_names_no_single_server() =>
        // Target reads "*" and the servers live in Subject, so nothing reading Target can mistake a batch
        // for an action on one server called something odd.
        Assert.Equal("*", PushActionTargets.AllServers);
}
