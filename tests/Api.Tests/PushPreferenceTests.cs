using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using TheKrystalShip.KGSM.Auth;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Per-account notification choices: the catalog view, sparse updates, and the two rules that decide
/// whether anything is actually delivered.
/// <para>
/// The default matters more than it looks. Storing only deviations means an untouched account has no
/// rows, so "absent" has to read as YES everywhere — in the view, in the fan-out filter, and for a
/// catalog event added after somebody last opened the page. Read it as NO in any one of those and
/// people quietly stop getting notifications they never turned off.
/// </para>
/// </summary>
public class PushPreferenceTests(AuthTestFactory factory) : IClassFixture<AuthTestFactory>
{
    private HttpClient Client()
    {
        HttpClient c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", factory.AccessToken(KgsmTier.Admin));
        return c;
    }

    private static JsonElement Event(JsonElement body, string id) =>
        body.GetProperty("events").EnumerateArray().First(e => e.GetProperty("id").GetString() == id);

    [Fact]
    public async Task The_whole_catalog_comes_back_and_defaults_to_on()
    {
        HttpClient c = Client();
        JsonElement body = await c.GetFromJsonAsync<JsonElement>("/api/v1/push/preferences");

        JsonElement[] events = [.. body.GetProperty("events").EnumerateArray()];
        Assert.NotEmpty(events);
        // An account that has never touched the page wants everything: subscribing was the opt-in.
        Assert.All(events, e => Assert.True(e.GetProperty("enabled").GetBoolean()));
        // Each carries its catalog copy so the UI never has to hardcode a description.
        Assert.All(events, e => Assert.False(string.IsNullOrWhiteSpace(e.GetProperty("title").GetString())));
    }

    [Fact]
    public async Task A_choice_is_saved_and_the_others_are_left_alone()
    {
        HttpClient c = Client();
        HttpResponseMessage res = await c.PatchAsJsonAsync("/api/v1/push/preferences",
            new { events = new[] { new { id = "online", enabled = false } } });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        // The PATCH answers with the full view, so a client never has to re-GET to render.
        JsonElement after = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(Event(after, "online").GetProperty("enabled").GetBoolean());
        Assert.True(Event(after, "crash").GetProperty("enabled").GetBoolean());

        JsonElement reread = await c.GetFromJsonAsync<JsonElement>("/api/v1/push/preferences");
        Assert.False(Event(reread, "online").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task A_choice_can_be_turned_back_on()
    {
        HttpClient c = Client();
        await c.PatchAsJsonAsync("/api/v1/push/preferences", new { events = new[] { new { id = "backup", enabled = false } } });
        HttpResponseMessage res = await c.PatchAsJsonAsync("/api/v1/push/preferences",
            new { events = new[] { new { id = "backup", enabled = true } } });

        JsonElement after = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(Event(after, "backup").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task An_unknown_event_is_refused_rather_than_stored()
    {
        HttpClient c = Client();
        HttpResponseMessage res = await c.PatchAsJsonAsync("/api/v1/push/preferences",
            new { events = new[] { new { id = "not-a-real-event", enabled = false } } });
        // A row nothing will ever read is worse than a rejection.
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task An_empty_patch_is_refused()
    {
        HttpClient c = Client();
        HttpResponseMessage res = await c.PatchAsJsonAsync("/api/v1/push/preferences", new { events = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task The_view_reports_the_hosts_own_rule_beside_the_personal_one()
    {
        HttpClient c = Client();
        // Admin turns an event off for the whole channel.
        await c.PatchAsJsonAsync("/api/v1/integrations/webpush",
            new { events = new[] { new { id = "installed", enabled = false } } });

        JsonElement body = await c.GetFromJsonAsync<JsonElement>("/api/v1/push/preferences");
        JsonElement installed = Event(body, "installed");

        // Both axes are reported: the person still wants it, the host is not carrying it. Showing only
        // the personal one would let somebody switch it on and hear nothing, with no explanation.
        Assert.False(installed.GetProperty("availableOnHost").GetBoolean());
        Assert.True(installed.GetProperty("enabled").GetBoolean());
        Assert.True(Event(body, "crash").GetProperty("availableOnHost").GetBoolean());

        // Leave the host as we found it — this factory's DB is shared across the class.
        await c.PatchAsJsonAsync("/api/v1/integrations/webpush",
            new { events = new[] { new { id = "installed", enabled = true } } });
    }

    [Fact]
    public async Task Preferences_need_a_session()
    {
        HttpClient anon = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/push/preferences")).StatusCode);
    }
}
