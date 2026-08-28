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
using TheKrystalShip.KGSM.Events;

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

    [Theory]
    [InlineData(PushActionKind.ServerStart, "start")]
    [InlineData(PushActionKind.ServerStop, "stop")]
    [InlineData(PushActionKind.ServerUpdate, "update")]
    public async Task Every_lifecycle_button_runs_the_panel_gates(string kind, string verb)
    {
        // A viewer is refused by the tier gate before anything is looked up, and the message names the
        // verb rather than a generic "not allowed" — the person is reading one line on a lock screen.
        KgsmIdentity who = Account("act-tier-" + verb, KgsmTier.Viewer);
        string handle = await Staged.StageAsync(kind, "factorio-01", who.Handle, who.Username, Endpoint, "Go");

        HttpResponseMessage res = await factory.CreateClient()
            .PostAsync($"/api/v1/notifications/actions/{handle}", From(Endpoint));

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Contains($"not allowed to {verb}", await res.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(PushActionKind.PlayerKick, "kick")]
    [InlineData(PushActionKind.PlayerBan, "ban")]
    public async Task Every_moderation_button_runs_the_tier_gate_too(string kind, string action)
    {
        // Removing a person from a game is a mutation like any other, and reaching it from a lock screen
        // must not be a way around the tier that guards the panel's own route.
        KgsmIdentity who = Account("act-mod-" + action, KgsmTier.Viewer);
        string handle = await Staged.StageAsync(
            kind, "factorio-01", who.Handle, who.Username, Endpoint, "Go", subject: "Ana");

        HttpResponseMessage res = await factory.CreateClient()
            .PostAsync($"/api/v1/notifications/actions/{handle}", From(Endpoint));

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Contains($"not allowed to {action} a player", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_moderation_handle_that_names_nobody_does_nothing()
    {
        // Belt-and-braces against a row staged without a subject: the target is a server, and a kick with
        // no player named must never be allowed to resolve to "whoever the resolver finds first".
        KgsmIdentity who = Account("act-mod-nosubject", KgsmTier.Operator);
        string handle = await Staged.StageAsync(
            PushActionKind.PlayerKick, "factorio-01", who.Handle, who.Username, Endpoint, "Kick");

        HttpResponseMessage res = await factory.CreateClient()
            .PostAsync($"/api/v1/notifications/actions/{handle}", From(Endpoint));

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Contains("did not name a player", await res.Content.ReadAsStringAsync());
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
    private static NotificationEvent Event(
        string catalogId, string? serverId, string? subject = null, string? player = null) =>
        new(catalogId, "irrelevant", serverId, AuditSeverity.Warn, "summary", DateTimeOffset.UtcNow, "evt_1",
            subject, player);

    private static ModerationCapability Can(bool kick, bool ban) => new(kick, ban, false, "name");

    /// <summary>
    /// ⚠ A reactor offer gets no lock-screen button, and the absence is the design.
    /// </summary>
    /// <remarks>
    /// Confirming one re-derives the condition on the leaf and shows the person what it found —
    /// sometimes that the thing is no longer applicable, which is the whole safety argument. A tap that
    /// authorised it from a lock screen would skip exactly that reading. The push exists to get somebody
    /// to open the offer before it expires, which the notification's own tap already does.
    /// </remarks>
    [Fact]
    public void A_reactor_offer_is_opened_rather_than_answered_from_a_lock_screen()
    {
        Assert.Empty(PushActionCatalog.For(Event("reactor_offer", null)));
        Assert.Empty(PushActionCatalog.For(Event("reactor_offer", "factorio-01")));
    }

    /// <summary>
    /// ⚠ Only the offer is announced, never the other three reactor events.
    /// </summary>
    /// <remarks>
    /// A decision is a judgment nobody has to answer; an action taken alone is already done; a
    /// resolution is somebody having answered, which would announce their own tap back to them. The
    /// offer is the one with something for a person to do — and the one whose whole point is reaching
    /// somebody who is not looking, because an unanswered offer expires.
    /// </remarks>
    [Fact]
    public void Only_the_reactors_offer_is_announced()
    {
        Assert.Equal("reactor_offer",
            NotificationCatalog.CatalogIdForAction(KgsmEventCatalog.NameOf<ReactorProposedEventData>()));

        Assert.Null(NotificationCatalog.CatalogIdForAction(KgsmEventCatalog.NameOf<ReactorDecidedEventData>()));
        Assert.Null(NotificationCatalog.CatalogIdForAction(KgsmEventCatalog.NameOf<ReactorResolvedEventData>()));
        Assert.Null(NotificationCatalog.CatalogIdForAction(KgsmEventCatalog.NameOf<ReactorActedEventData>()));
    }

    /// <summary>
    /// An offer arrives by default, like everything the fleet does on its own.
    /// </summary>
    /// <remarks>
    /// The two opt-in events are bounded by how popular a server is; this one is bounded by how often a
    /// rule fires, which an operator already controls by writing the rule. Defaulting it off would mean
    /// staging an offer and being the only one who knows.
    /// </remarks>
    [Fact]
    public void An_offer_is_announced_unless_somebody_switches_it_off()
    {
        Assert.True(NotificationCatalog.IsKnown("reactor_offer"));
        Assert.True(NotificationCatalog.DefaultRule("reactor_offer").Enabled);
    }

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

    [Fact]
    public void Being_told_a_server_went_down_offers_to_put_it_back()
    {
        PushActionOffer only = Assert.Single(PushActionCatalog.For(Event("offline", "factorio-01")));
        Assert.Equal(PushActionKind.ServerStart, only.Kind);
    }

    [Fact]
    public void A_crash_offers_STOP_not_restart()
    {
        // The watchdog is already restarting it — that is why a crash notification arrives repeatedly —
        // so a Restart button would offer to do the thing that is happening anyway. Stop is the one that
        // changes the desired state and breaks the loop.
        PushActionOffer only = Assert.Single(PushActionCatalog.For(Event("crash", "factorio-01")));
        Assert.Equal(PushActionKind.ServerStop, only.Kind);
    }

    [Fact]
    public void A_give_up_offers_START_because_the_watchdog_has_stopped_trying()
    {
        // The mirror of the crash above: there, the supervisor is restarting it and Stop is the only
        // thing that changes anything. Here it has given up, so the server is already down and Stop
        // would ask for what already is.
        PushActionOffer only = Assert.Single(PushActionCatalog.For(Event("crash_loop", "factorio-01")));
        Assert.Equal(PushActionKind.ServerStart, only.Kind);
    }

    [Fact]
    public void An_empty_server_offers_to_stop_it()
    {
        PushActionOffer only = Assert.Single(PushActionCatalog.For(Event("server_empty", "factorio-01")));
        Assert.Equal(PushActionKind.ServerStop, only.Kind);
    }

    [Fact]
    public void A_join_offers_kick_before_ban_and_names_the_player()
    {
        IReadOnlyList<PushActionOffer> offers =
            PushActionCatalog.For(Event("player_join", "romestead", player: "Ana"), Can(kick: true, ban: true));

        // Kick first: two is the button ceiling on the platform these are read on, so the order here is
        // the order that survives, and the reversible action is the one to keep.
        Assert.Collection(offers,
            o => Assert.Equal(PushActionKind.PlayerKick, o.Kind),
            o => Assert.Equal(PushActionKind.PlayerBan, o.Kind));
        Assert.All(offers, o =>
        {
            Assert.Equal("romestead", o.Target);
            Assert.Equal("Ana", o.Subject);
        });
    }

    [Fact]
    public void A_game_that_declares_no_ban_is_not_offered_one()
    {
        // The blueprint's placeholder IS the contract. Offering a button the engine will refuse promises
        // something this host cannot do.
        PushActionOffer only = Assert.Single(
            PushActionCatalog.For(Event("player_join", "romestead", player: "Ana"), Can(kick: true, ban: false)));
        Assert.Equal(PushActionKind.PlayerKick, only.Kind);
    }

    [Fact]
    public void A_join_offers_nothing_when_moderation_could_not_be_established() =>
        // Not knowing what a game supports is treated exactly like knowing it supports nothing: being
        // wrong here removes a real person from a game.
        Assert.Empty(PushActionCatalog.For(Event("player_join", "romestead", player: "Ana"), moderation: null));

    [Fact]
    public void A_join_that_names_nobody_offers_nothing() =>
        Assert.Empty(PushActionCatalog.For(Event("player_join", "romestead"), Can(kick: true, ban: true)));

    [Fact]
    public void A_service_that_went_down_offers_to_restart_it()
    {
        PushActionOffer only = Assert.Single(
            PushActionCatalog.For(Event("leaf_down", null, player: "monitor")));
        Assert.Equal(PushActionKind.LeafRestart, only.Kind);
        Assert.Equal("monitor", only.Target);
    }

    [Fact]
    public void This_API_is_not_offered_a_button_to_restart_itself() =>
        // Restarting it would kill the request doing the restarting, so the reply would never arrive.
        Assert.Empty(PushActionCatalog.For(Event("leaf_down", null, player: "api")));

    [Fact]
    public void A_recovered_service_offers_nothing() =>
        Assert.Empty(PushActionCatalog.For(Event("leaf_up", null, player: "monitor")));

    [Fact]
    public void Somebody_waiting_to_be_let_in_offers_to_let_them_in()
    {
        PushActionOffer only = Assert.Single(
            PushActionCatalog.For(Event("awaiting_approval", null, player: "usr_abc")));
        Assert.Equal(PushActionKind.UserApprove, only.Kind);
        Assert.Equal("usr_abc", only.Target);
    }

    [Theory]
    [InlineData("online")]
    [InlineData("backup")]
    [InlineData("update")]
    [InlineData("installed")]
    // A recovery needs no reply, and there is no honest one-tap remedy for the rest.
    [InlineData("threshold_clear")]
    public void The_rest_offer_nothing(string catalogId) =>
        Assert.Empty(PushActionCatalog.For(Event(catalogId, "srv", "some/condition/")));

    [Theory]
    [InlineData(PushActionKind.PlayerKick, ModerationAction.Kick)]
    [InlineData(PushActionKind.PlayerBan, ModerationAction.Ban)]
    public void Each_moderation_kind_names_the_action_it_runs(string kind, string action) =>
        Assert.Equal(action, PushActionKind.ModerationFor(kind));

    [Theory]
    [InlineData(PushActionKind.ServerUpdate)]
    [InlineData(PushActionKind.ServerStart)]
    [InlineData(PushActionKind.ServerStop)]
    [InlineData(PushActionKind.ConditionSnooze)]
    [InlineData(PushActionKind.PlayerKick)]
    [InlineData(PushActionKind.PlayerBan)]
    [InlineData(PushActionKind.LeafRestart)]
    [InlineData(PushActionKind.UserApprove)]
    public void Every_kind_the_catalog_can_stage_is_one_the_store_will_hand_back(string kind) =>
        // The store refuses to redeem a row whose kind this build does not know, so a kind added to the
        // catalog and not to IsKnown would stage handles that silently do nothing.
        Assert.True(PushActionKind.IsKnown(kind));

    [Fact]
    public void A_lifecycle_kind_is_not_a_moderation_action() =>
        Assert.Null(PushActionKind.ModerationFor(PushActionKind.ServerStop));

    [Theory]
    [InlineData(PushActionKind.ServerUpdate, CommandVerb.Update)]
    [InlineData(PushActionKind.ServerStart, CommandVerb.Start)]
    [InlineData(PushActionKind.ServerStop, CommandVerb.Stop)]
    public void Each_server_kind_names_the_engine_verb_it_runs(string kind, string verb) =>
        Assert.Equal(verb, PushActionKind.VerbFor(kind));

    [Fact]
    public void A_snooze_is_not_an_engine_verb() =>
        Assert.Null(PushActionKind.VerbFor(PushActionKind.ConditionSnooze));

    [Fact]
    public void An_update_with_no_server_named_offers_nothing() =>
        Assert.Empty(PushActionCatalog.For(Event("update_available", null)));

    [Fact]
    public void A_breach_with_no_condition_named_offers_nothing() =>
        // Without a subject there is nothing specific to silence, and snoozing "the host" is a
        // different, louder thing than the button says.
        Assert.Empty(PushActionCatalog.For(Event("threshold_breach", null)));
}
