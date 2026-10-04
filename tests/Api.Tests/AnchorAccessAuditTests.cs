using System.Text;
using System.Text.Json;

using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.Auth.Journal;
using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// The auth anchor's record of who may do what, read back as audit rows: an assignment, a role or a
/// permission changing, the catalog moving, a service's requirement decided.
/// </summary>
/// <remarks>
/// Payloads come from <see cref="AuthEventPayloads"/> — the writer the anchor calls — so these check the
/// agreement between the two ends rather than a shape this file made up.
/// </remarks>
public sealed class AnchorAccessAuditTests
{
    private const string HostId = "hotrod";
    private static readonly DateTimeOffset When = DateTimeOffset.Parse("2026-09-26T12:00:00Z");

    private static T Roundtrip<T>(Action<Utf8JsonWriter> payload, string actor) where T : KgsmEventDataBase
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            payload(writer);
            writer.WriteEndObject();
        }

        T data = JsonSerializer.Deserialize<T>(Encoding.UTF8.GetString(buffer.ToArray()),
            new JsonSerializerOptions(JsonSerializerDefaults.General))!;
        data.Timestamp = When;
        data.Actor = actor;
        data.Origin = AuditOrigin.Ui;
        return data;
    }

    [Fact]
    public void A_grant_names_who_gave_whom_which_role_where()
    {
        AssignmentEventData d = Roundtrip<AssignmentEventData>(
            AuthEventPayloads.Assignment("asg_1", "usr_alice", "alice", "role_hosts", "Hosts",
                "instance:walter/terraria#9f3c", 12), "local:owner");

        AuditWrite row = AuditMapping.FromAssignmentEvent(d, AuthEvents.AssignmentGranted, HostId);

        Assert.Equal("owner gave 'alice' the 'Hosts' role on terraria on walter", row.Summary);
        Assert.Equal(AuditSeverity.Warn, row.Severity);
        Assert.Equal("usr_alice", row.Target!.Id);
        Assert.Equal("12", row.Meta!["authorityVersion"]);
        Assert.Equal("instance:walter/terraria#9f3c", row.Meta["scope"]);
    }

    [Fact]
    public void A_revocation_reads_as_one()
    {
        AssignmentEventData d = Roundtrip<AssignmentEventData>(
            AuthEventPayloads.Assignment("asg_1", "usr_alice", null, "role_hosts", "Hosts", "cluster", 13), "local:owner");

        Assert.Equal("owner took the 'Hosts' role from usr_alice cluster-wide",
            AuditMapping.FromAssignmentEvent(d, AuthEvents.AssignmentRevoked, HostId).Summary);
    }

    [Theory]
    [InlineData(AuthEvents.RoleChanged, "owner changed the role 'Hosts'", "role")]
    [InlineData(AuthEvents.RoleRemoved, "owner deleted the role 'Hosts'", "role")]
    [InlineData(AuthEvents.PermissionChanged, "owner changed the permission 'Hosts'", "permission")]
    [InlineData(AuthEvents.PermissionRemoved, "owner deleted the permission 'Hosts'", "permission")]
    public void A_role_or_permission_change_names_which(string type, string summary, string kind)
    {
        AuthorityRecordEventData d = Roundtrip<AuthorityRecordEventData>(AuthEventPayloads.Authority("x_1", "Hosts", 5), "local:owner");

        AuditWrite row = AuditMapping.FromAuthorityRecordEvent(d, type, HostId);

        Assert.Equal(summary, row.Summary);
        Assert.Equal(kind, row.Target!.Kind);
    }

    [Fact]
    public void A_catalog_arrival_says_nothing_holds_it_yet()
    {
        CatalogEventData d = Roundtrip<CatalogEventData>(
            AuthEventPayloads.Catalog("walter", ["reactor:rules.write", "reactor:rules.read"], [], 3), "system:walter");

        AuditWrite row = AuditMapping.FromCatalogEvent(d, HostId);

        Assert.Equal("walter declared 2 new actions, not yet in any permission", row.Summary);
        Assert.Equal(AuditSeverity.Info, row.Severity);
        Assert.Equal("reactor:rules.write, reactor:rules.read", row.Meta!["added"]);
    }

    [Fact]
    public void An_automatic_approval_says_nobody_chose_it()
    {
        ServiceRequirementEventData d = Roundtrip<ServiceRequirementEventData>(
            AuthEventPayloads.Requirement("usr_svc", "svc:reactor@walter", "kgsm:server.restart", "node:walter", automatic: true, 4),
            "system:walter");

        AuditWrite row = AuditMapping.FromServiceRequirementEvent(d, AuthEvents.ServiceRequirementApproved, HostId);

        Assert.Equal("svc:reactor@walter was allowed kgsm:server.restart on walter automatically", row.Summary);
        Assert.Equal("true", row.Meta!["automatic"]);
    }

    [Fact]
    public void Every_access_event_is_shaped_rather_than_dropped()
    {
        foreach (string type in new[]
                 {
                     AuthEvents.AssignmentGranted, AuthEvents.AssignmentRevoked, AuthEvents.RoleChanged,
                     AuthEvents.RoleRemoved, AuthEvents.PermissionChanged, AuthEvents.PermissionRemoved,
                     AuthEvents.CatalogChanged, AuthEvents.ServiceRequirementApproved,
                     AuthEvents.ServiceRequirementRevoked,
                 })
        {
            Assert.True(KgsmEventCatalog.Describe(type).Known, $"{type} is not in the engine's catalog");
        }
    }
}
