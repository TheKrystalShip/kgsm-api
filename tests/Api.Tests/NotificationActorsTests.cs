using System.Net.Http.Headers;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Notifications;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// A notification is told, and a button is tapped, when its person has no token to present. What they may
/// do is what their token last listed, trusted for a bounded time.
/// </summary>
public sealed class NotificationActorsTests(AuthTestFactory factory) : IClassFixture<AuthTestFactory>
{
    private (WebApplicationFactory<Program> Node, ManualTime Clock) NodeOnAClock()
    {
        var clock = new ManualTime(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
        WebApplicationFactory<Program> node = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        }));
        return (node, clock);
    }

    private static async Task SeenAsync(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me/notifications");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        (await client.SendAsync(request)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task A_person_is_told_what_their_token_last_listed_they_may_read()
    {
        (WebApplicationFactory<Program> node, _) = NodeOnAClock();
        using WebApplicationFactory<Program> _node = node;
        TestPerson reader = TestIdentity.PersonFor("notified-reader");
        TestPerson nobody = TestIdentity.PersonFor("notified-nobody");
        using HttpClient client = node.CreateClient();

        await SeenAsync(client, factory.AccessTokenFor(reader, Persona.Reader));
        await SeenAsync(client, factory.AccessTokenFor(nobody, Persona.None));

        IReadOnlyList<string> audience = node.Services.GetRequiredService<NotificationActors>().Holding(ActionIds.ServerRead);
        Assert.Contains(reader.Subject, audience);
        Assert.DoesNotContain(nobody.Subject, audience);
    }

    [Fact]
    public async Task A_tap_just_past_the_action_bound_is_refused()
    {
        (WebApplicationFactory<Program> node, ManualTime clock) = NodeOnAClock();
        using WebApplicationFactory<Program> _node = node;
        TestPerson reader = TestIdentity.PersonFor("notified-tap");
        using HttpClient client = node.CreateClient();
        await SeenAsync(client, factory.AccessTokenFor(reader, Persona.Reader));
        NotificationActors actors = node.Services.GetRequiredService<NotificationActors>();

        clock.Advance(NotificationActors.ActionFreshness - TimeSpan.FromMinutes(1));
        Assert.True(actors.AllowsTap(reader.Subject, ActionIds.ServerRead));

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.False(actors.AllowsTap(reader.Subject, ActionIds.ServerRead));
        Assert.Null(actors.Known(reader.Subject, NotificationActors.ActionFreshness));
    }

    [Fact]
    public async Task A_person_last_seen_two_days_ago_is_still_told_but_cannot_tap()
    {
        (WebApplicationFactory<Program> node, ManualTime clock) = NodeOnAClock();
        using WebApplicationFactory<Program> _node = node;
        TestPerson reader = TestIdentity.PersonFor("notified-two-days");
        using HttpClient client = node.CreateClient();
        await SeenAsync(client, factory.AccessTokenFor(reader, Persona.Reader));
        NotificationActors actors = node.Services.GetRequiredService<NotificationActors>();

        clock.Advance(TimeSpan.FromDays(2));

        Assert.Contains(reader.Subject, actors.Holding(ActionIds.ServerRead));
        Assert.False(actors.AllowsTap(reader.Subject, ActionIds.ServerRead));
    }

    [Fact]
    public async Task A_person_is_not_told_once_unseen_for_the_freshness_window()
    {
        (WebApplicationFactory<Program> node, ManualTime clock) = NodeOnAClock();
        using WebApplicationFactory<Program> _node = node;
        TestPerson reader = TestIdentity.PersonFor("notified-stale");
        using HttpClient client = node.CreateClient();
        await SeenAsync(client, factory.AccessTokenFor(reader, Persona.Reader));
        NotificationActors actors = node.Services.GetRequiredService<NotificationActors>();

        clock.Advance(NotificationActors.Freshness + TimeSpan.FromMinutes(1));

        Assert.DoesNotContain(reader.Subject, actors.Holding(ActionIds.ServerRead));
    }

    [Fact]
    public async Task A_role_taken_away_applies_the_next_time_the_person_is_seen()
    {
        (WebApplicationFactory<Program> node, _) = NodeOnAClock();
        using WebApplicationFactory<Program> _node = node;
        TestPerson person = TestIdentity.PersonFor("notified-demoted");
        using HttpClient client = node.CreateClient();
        NotificationActors actors = node.Services.GetRequiredService<NotificationActors>();

        await SeenAsync(client, factory.AccessTokenFor(person, Persona.Runner));
        Assert.True(actors.AllowsTap(person.Subject, ActionIds.ServerStart));

        await SeenAsync(client, factory.AccessTokenFor(person, Persona.Reader));
        Assert.False(actors.AllowsTap(person.Subject, ActionIds.ServerStart));
    }

    [Fact]
    public void A_buttons_life_is_within_the_action_bound_and_so_within_a_day() =>
        Assert.True(PushActionCatalog.ButtonLifetime <= NotificationActors.ActionFreshness);

    [Fact]
    public void The_action_bound_is_a_day_and_tighter_than_the_telling_bound()
    {
        Assert.True(NotificationActors.ActionFreshness <= TimeSpan.FromHours(24));
        Assert.True(NotificationActors.ActionFreshness < NotificationActors.Freshness);
    }
}
