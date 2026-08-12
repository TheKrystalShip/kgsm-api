using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.Api.Services.Integrations;
using TheKrystalShip.Api.Services.Integrations.WebPush;
using TheKrystalShip.KGSM.Auth;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// The buttons on a push notification: what gets offered, what a handle can be redeemed for, and what
/// it cannot.
/// <para>
/// The refusals are the point of this file. The redemption route is the API's one anonymous write — a
/// service worker holds no session, so the handle stands in for a bearer — and everything that keeps
/// that sound is a negative: single-use, bound to its device, and a tier resolved at the tap rather
/// than carried from staging time.
/// </para>
/// </summary>
public class NotificationActionTests(AuthTestFactory factory) : IClassFixture<AuthTestFactory>
{
    private const string Endpoint = "https://push.test/one";
    private const string OtherEndpoint = "https://push.test/two";

    private PushActionStore Staged => factory.Services.GetRequiredService<PushActionStore>();

    // A real account, because a redemption resolves the tier from the store rather than trusting
    // anything the staged row says about it.
    private KgsmIdentity Account(string subject, KgsmTier tier)
    {
        var identity = new KgsmIdentity(KgsmActorProvider.Discord, subject, "tapper-" + subject, "Tapper", null, []);
        factory.SetAccount(identity, tier);
        return identity;
    }

    private async Task<string> StageUpdateAsync(KgsmIdentity who, string server = "factorio-01", string endpoint = Endpoint) =>
        await Staged.StageAsync(
            PushActionKind.ServerUpdate, server, who.Handle, who.Username, endpoint, "Update now");

    private static HttpContent From(string? endpoint) => JsonContent.Create(new { endpoint });

    [Fact]
    public async Task The_route_takes_no_bearer_at_all()
    {
        // Load-bearing: the worker has no token to send, so this must reach the controller rather than
        // the challenge. An unknown handle answering 404 is the proof — a 401 would mean the whole
        // mechanism is unreachable from where it is used.
        HttpClient c = factory.CreateClient();
        Assert.Null(c.DefaultRequestHeaders.Authorization);

        HttpResponseMessage res = await c.PostAsync(
            "/api/v1/notifications/actions/0123456789abcdef0123456789abcdef", From(Endpoint));

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task A_handle_is_redeemed_once()
    {
        KgsmIdentity who = Account("act-single", KgsmTier.Operator);
        string handle = await StageUpdateAsync(who);
        HttpClient c = factory.CreateClient();

        // The first redemption gets as far as the server lookup — there is no engine on the test host,
        // so it refuses with that rather than with anything about the handle. Which is the point: the
        // handle was spent.
        HttpResponseMessage first = await c.PostAsync($"/api/v1/notifications/actions/{handle}", From(Endpoint));
        Assert.Equal(HttpStatusCode.Forbidden, first.StatusCode);
        Assert.Contains("not on this host", await first.Content.ReadAsStringAsync());

        HttpResponseMessage second = await c.PostAsync($"/api/v1/notifications/actions/{handle}", From(Endpoint));
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
    }

    [Fact]
    public async Task A_handle_presented_by_the_wrong_device_is_refused_AND_left_standing()
    {
        KgsmIdentity who = Account("act-device", KgsmTier.Operator);
        string handle = await StageUpdateAsync(who);
        HttpClient c = factory.CreateClient();

        HttpResponseMessage wrong = await c.PostAsync($"/api/v1/notifications/actions/{handle}", From(OtherEndpoint));
        Assert.Equal(HttpStatusCode.NotFound, wrong.StatusCode);

        // Still there. Consuming somebody else's handle on a wrong guess would let anyone destroy an
        // action its owner is about to tap, which is a denial of service dressed as a security check.
        HttpResponseMessage right = await c.PostAsync($"/api/v1/notifications/actions/{handle}", From(Endpoint));
        Assert.Equal(HttpStatusCode.Forbidden, right.StatusCode);
        Assert.Contains("not on this host", await right.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_handle_with_no_device_named_redeems_nothing()
    {
        KgsmIdentity who = Account("act-nodevice", KgsmTier.Operator);
        string handle = await StageUpdateAsync(who);
        HttpClient c = factory.CreateClient();

        HttpResponseMessage res = await c.PostAsync($"/api/v1/notifications/actions/{handle}", From(null));
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task The_tier_is_resolved_at_the_tap_not_at_staging()
    {
        // Staged while they could act, redeemed after they were demoted. The staged row says nothing
        // about authority, so this is the account store's answer at the moment of the tap.
        KgsmIdentity who = Account("act-demoted", KgsmTier.Operator);
        string handle = await StageUpdateAsync(who);
        factory.SetAccount(who, KgsmTier.Viewer);

        HttpResponseMessage res = await factory.CreateClient()
            .PostAsync($"/api/v1/notifications/actions/{handle}", From(Endpoint));

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Contains("not allowed", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_switched_off_account_redeems_nothing()
    {
        KgsmIdentity who = Account("act-disabled", KgsmTier.Admin);
        string handle = await StageUpdateAsync(who);
        factory.SetAccount(who, KgsmTier.Admin, TheKrystalShip.KGSM.Auth.Users.UserStatus.Disabled);

        HttpResponseMessage res = await factory.CreateClient()
            .PostAsync($"/api/v1/notifications/actions/{handle}", From(Endpoint));

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Contains("switched off", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Snoozing_silences_that_condition_for_that_person()
    {
        KgsmIdentity who = Account("act-snooze", KgsmTier.Viewer);
        const string condition = "host-temp/k10temp/Tctl/";
        const string snoozeEndpoint = "https://push.test/snooze-device";

        // The device has to be registered, because the snooze is filed against the account the row
        // names — the same subject the fan-out gate reads.
        var subscriptions = factory.Services.GetRequiredService<PushSubscriptionStore>();
        await subscriptions.SaveAsync(new PushSubscriptionEntity
        {
            Endpoint = snoozeEndpoint,
            UserSubject = who.Subject,
            UserHandle = who.Handle,
            Username = who.Username,
            P256dh = "x",
            Auth = "y",
            MaxActions = 2,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        string handle = await Staged.StageAsync(
            PushActionKind.ConditionSnooze, condition, who.Handle, who.Username, snoozeEndpoint, "Snooze 4h");

        HttpResponseMessage res = await factory.CreateClient()
            .PostAsync($"/api/v1/notifications/actions/{handle}", From(snoozeEndpoint));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var snoozes = factory.Services.GetRequiredService<PushSnoozeStore>();
        IReadOnlySet<(string, string)> active = await snoozes.ActiveAsync();
        Assert.Contains((who.Subject, condition), active);
        // Only theirs, and only that condition — a snooze is not a mute button on the whole event.
        Assert.DoesNotContain(("someone-else", condition), active);
    }
}

/// <summary>
/// The origin a redeemed button carries, and who is allowed to claim it.
/// </summary>
public class NotificationOriginTests(AuthTestFactory factory) : IClassFixture<AuthTestFactory>
{
    [Fact]
    public void The_notification_origin_is_in_the_closed_set()
        // Load-bearing rather than cosmetic: AuditMapping normalizes an unrecognised origin to null, so
        // a value stamped on an engine call but missing from this set comes back off the echo having
        // lost the whole provenance — silently, at runtime.
        => Assert.True(AuditOrigin.IsKnown(AuditOrigin.Notification));

    [Fact]
    public void It_survives_the_engine_echo()
        // The whole provenance passes through here: the API stamps the origin onto the engine call, the
        // engine puts it on its event verbatim, and this is what shapes the event back into a row. A
        // value the closed set does not contain is normalized to null right here, so the round trip is
        // the assertion that matters — not the constant existing.
        => Assert.Equal(AuditOrigin.Notification, AuditMapping.NormalizeOrigin("Notification"));

    [Fact]
    public void No_caller_may_declare_it()
        // Reserved like `system`. A request naming it would be claiming to be a redemption this API
        // performed, which is the one claim it has no way to check.
        => Assert.False(AuditOrigin.IsCallerDeclarable(AuditOrigin.Notification));

    [Fact]
    public async Task A_command_declaring_it_is_refused()
    {
        HttpClient c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", factory.AccessToken(KgsmTier.Operator));

        HttpResponseMessage res = await c.PostAsJsonAsync(
            "/api/v1/servers/factorio-01/commands", new { verb = "update", origin = "notification" });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}

/// <summary>Which events offer a button, and which deliberately do not.</summary>
public class PushActionCatalogTests
{
    private static NotificationEvent Event(string catalogId, string? serverId, string? subject = null) =>
        new(catalogId, "irrelevant", serverId, AuditSeverity.Warn, "summary", DateTimeOffset.UtcNow, "evt_1", subject);

    [Fact]
    public void An_available_update_offers_to_apply_it()
    {
        PushActionOffer only = Assert.Single(PushActionCatalog.For(Event("update_available", "factorio-01")));
        Assert.Equal(PushActionKind.ServerUpdate, only.Kind);
        Assert.Equal("factorio-01", only.Target);
    }

    [Fact]
    public void A_breach_offers_to_silence_the_CONDITION_not_the_host()
    {
        PushActionOffer only = Assert.Single(
            PushActionCatalog.For(Event("threshold_breach", null, "host-temp/k10temp/Tctl/")));
        Assert.Equal(PushActionKind.ConditionSnooze, only.Kind);
        Assert.Equal("host-temp/k10temp/Tctl/", only.Target);
    }

    [Theory]
    [InlineData("crash")]
    [InlineData("online")]
    [InlineData("offline")]
    [InlineData("backup")]
    // A recovery needs no reply, and there is no honest one-tap remedy for the rest.
    [InlineData("threshold_clear")]
    public void Most_events_offer_nothing(string catalogId) =>
        Assert.Empty(PushActionCatalog.For(Event(catalogId, "srv", "some/condition/")));

    [Fact]
    public void An_update_with_no_server_named_offers_nothing() =>
        Assert.Empty(PushActionCatalog.For(Event("update_available", null)));

    [Fact]
    public void A_breach_with_no_condition_named_offers_nothing() =>
        // Without a subject there is nothing specific to silence, and snoozing "the host" is a
        // different, louder thing than the button says.
        Assert.Empty(PushActionCatalog.For(Event("threshold_breach", null)));
}
