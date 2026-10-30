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
    public async Task A_person_stops_being_told_once_their_token_has_not_been_seen_for_the_freshness_window()
    {
        (WebApplicationFactory<Program> node, ManualTime clock) = NodeOnAClock();
        using WebApplicationFactory<Program> _node = node;
        TestPerson reader = TestIdentity.PersonFor("notified-stale");
        using HttpClient client = node.CreateClient();
        await SeenAsync(client, factory.AccessTokenFor(reader, Persona.Reader));
        NotificationActors actors = node.Services.GetRequiredService<NotificationActors>();
        Assert.True(actors.Allows(reader.Subject, ActionIds.ServerRead));

        clock.Advance(NotificationActors.Freshness + TimeSpan.FromMinutes(1));

        Assert.False(actors.Allows(reader.Subject, ActionIds.ServerRead));
        Assert.DoesNotContain(reader.Subject, actors.Holding(ActionIds.ServerRead));
        Assert.Null(actors.Known(reader.Subject));
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
        Assert.True(actors.Allows(person.Subject, ActionIds.ServerStart));

        await SeenAsync(client, factory.AccessTokenFor(person, Persona.Reader));
        Assert.False(actors.Allows(person.Subject, ActionIds.ServerStart));
    }
}
