using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.Api.Services.Integrations;
using TheKrystalShip.Api.Services.Notifications;
using TheKrystalShip.Notifications;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// What a node tells people through the shared notification library: the kinds, the line and the link a
/// row carries, and the routes that serve them.
/// </summary>
public class FleetNotificationTests(AuthTestFactory factory) : IClassFixture<AuthTestFactory>
{
    private static NotificationEvent Event(string catalogId, string action, string? serverId, string auditId = "audit_1") =>
        new(catalogId, action, serverId, AuditSeverity.Info, "summary", DateTimeOffset.UnixEpoch, auditId);

    [Fact]
    public void Every_catalog_event_is_a_kind_and_the_two_crowd_events_start_off()
    {
        Assert.Equal(NotificationCatalog.Events.Select(e => e.Id), FleetNotifications.Kinds.Select(k => k.Id));
        Assert.All(FleetNotifications.Kinds, k => Assert.True(k.Switchable));

        string[] off = [.. FleetNotifications.Kinds.Where(k => !k.EnabledByDefault).Select(k => k.Id)];
        Assert.Equal(["player_join", "server_empty"], off.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_row_is_keyed_by_its_occurrence()
    {
        Assert.Equal("audit:audit_1", FleetNotifications.KeyOf(Event("crash", "server.crashed", "a")));

        string derived = FleetNotifications.KeyOf(Event("leaf_down", "leaf.down", null, auditId: "") with { SubjectKey = "leaf/x" });
        Assert.StartsWith("leaf.down:leaf/x:", derived);
    }

    [Fact]
    public void A_row_opens_the_server_the_alerts_or_the_audit_log()
    {
        Assert.Equal("/#/servers/factorio-01", FleetNotifications.UrlOf(Event("crash", "server.crashed", "factorio-01")));
        Assert.Equal("/#/alerts", FleetNotifications.UrlOf(Event("threshold_breach", "host.threshold.breached", null)));
        Assert.Equal("/#/alerts?serverId=a", FleetNotifications.UrlOf(Event("threshold_clear", "host.threshold.cleared", "a")));
        Assert.Equal("/#/audit", FleetNotifications.UrlOf(Event("leaf_down", "leaf.down", null)));
    }

    [Fact]
    public void A_button_argument_round_trips_whatever_the_subject_contains()
    {
        ButtonArgument? read = ButtonArgument.Unpack(ButtonArgument.Pack("romestead", "Name, With: Odd|Chars"));
        Assert.Equal(new ButtonArgument("romestead", "Name, With: Odd|Chars"), read);
        Assert.Null(ButtonArgument.Unpack("not json"));
    }

    [Fact]
    public async Task An_unknown_button_handle_answers_200_and_says_it_is_no_longer_valid()
    {
        HttpClient c = factory.CreateClient();
        HttpResponseMessage res = await c.PostAsync(
            "/api/v1/notifications/actions/0123456789abcdef0123456789abcdef", JsonContent.Create(new { }));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("\"ok\":false", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_person_reads_their_own_notifications_and_a_stranger_is_refused()
    {
        HttpClient anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/v1/me/notifications")).StatusCode);

        HttpClient c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.AccessToken(Persona.Reader));
        HttpResponseMessage page = await c.GetAsync("/api/v1/me/notifications");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);

        string kinds = await c.GetStringAsync("/api/v1/me/notifications/kinds");
        Assert.Contains("\"id\":\"crash\"", kinds);
    }
}
