using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TheKrystalShip.Api.Services.Preferences;
using TheKrystalShip.KGSM.Auth;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// The general per-account preference store — keys nothing here understands, stored per device, with an
/// account switch that makes one device's set authoritative for all of them.
/// <para>
/// The load-bearing facts are the ones a cluster will later depend on and a browser depends on now: a
/// write bumps a version that is monotonic per (account, key) rather than per row; the device says who
/// it is and is refused when it does not; enabling sync overwrites the other devices from the source;
/// and disabling seeds every device from the synced record, so nobody watches their dashboard empty
/// itself because they turned a switch off.
/// </para>
/// </summary>
/// <remarks>
/// Every token this factory mints is the same identity, so the whole class shares one account's rows —
/// which is exactly the surface under test and cannot be isolated away. Two habits keep the tests
/// independent of each other: each names its own device ids and its own keys, and each sets the sync
/// switch it needs rather than inheriting whatever the previous test left. Assertions are per key, never
/// on the size of the returned set, because the sync rewrites deliberately touch every device.
/// </remarks>
public sealed class UserPreferencesTests(AuthTestFactory factory) : IClassFixture<AuthTestFactory>
{
    private const string DeviceHeader = "X-Krystal-Device";

    private HttpClient Client(string? device)
    {
        HttpClient c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", factory.AccessToken(KgsmTier.Viewer));
        if (device is not null) c.DefaultRequestHeaders.Add(DeviceHeader, device);
        return c;
    }

    private static Task<HttpResponseMessage> Put(HttpClient c, string key, object value) =>
        c.PutAsJsonAsync($"/api/v1/me/preferences/{key}", new { value });

    private static async Task<JsonElement> Json(HttpResponseMessage resp) =>
        JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;

    /// <summary>The set a device reads, as key → record.</summary>
    private static async Task<JsonElement> Prefs(HttpClient c) =>
        (await Json(await c.GetAsync("/api/v1/me/preferences"))).GetProperty("preferences");

    private static Task<HttpResponseMessage> Sync(HttpClient c, bool enabled) =>
        c.PutAsJsonAsync("/api/v1/me/preferences/sync", new { enabled });

    /// <summary>The rows one device actually holds, whatever the switch says — the only way to see the
    /// overwrite an enable performs, since while sync is on every device READS the synced record.</summary>
    private async Task<IReadOnlyList<PreferenceRow>> Slot(string device) =>
        await factory.Services.GetRequiredService<UserPreferenceStore>()
            .SlotAsync(FakeDiscordResolver.Identity.Handle, device);

    [Fact]
    public async Task A_write_comes_back_on_the_device_that_made_it()
    {
        HttpClient c = Client("dev_write_a");
        await Sync(c, false);

        HttpResponseMessage put = await Put(c, "t.write", new { cols = 12 });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        JsonElement stored = (await Prefs(c)).GetProperty("t.write");
        Assert.Equal(12, stored.GetProperty("value").GetProperty("cols").GetInt32());
        // The device is echoed so a client can tell the answer is for the machine it just named.
        Assert.Equal("dev_write_a", stored.GetProperty("originDevice").GetString());
    }

    [Fact]
    public async Task A_value_is_opaque_json_and_survives_verbatim()
    {
        HttpClient c = Client("dev_opaque");
        await Sync(c, false);

        // A key this API has never heard of behaves exactly like one it has — that is the property that
        // makes a new preference cost no backend change.
        await Put(c, "t.opaque", new object[] { new { i = "w_1", type = "leaf.logs", w = 12 }, 7, "x" });

        JsonElement value = (await Prefs(c)).GetProperty("t.opaque").GetProperty("value");
        Assert.Equal(JsonValueKind.Array, value.ValueKind);
        Assert.Equal("leaf.logs", value[0].GetProperty("type").GetString());
        Assert.Equal(7, value[1].GetInt32());
    }

    [Fact]
    public async Task Each_write_takes_the_next_version_for_that_key_whichever_device_wrote_it()
    {
        HttpClient a = Client("dev_ver_a");
        HttpClient b = Client("dev_ver_b");
        await Sync(a, false);

        Assert.Equal(1, (await Json(await Put(a, "t.version", 1))).GetProperty("version").GetInt64());
        Assert.Equal(2, (await Json(await Put(a, "t.version", 2))).GetProperty("version").GetInt64());

        // The counter is per (account, key), NOT per row: a second device writing its OWN row still
        // takes the next number, because that number is what a merge between nodes compares.
        JsonElement fromB = await Json(await Put(b, "t.version", 3));
        Assert.Equal(3, fromB.GetProperty("version").GetInt64());
        Assert.Equal("dev_ver_b", fromB.GetProperty("originDevice").GetString());

        // Untouched key, untouched counter.
        Assert.Equal(1, (await Json(await Put(a, "t.version.other", 1))).GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task With_sync_off_a_device_does_not_see_another_devices_rows()
    {
        HttpClient a = Client("dev_iso_a");
        HttpClient b = Client("dev_iso_b");
        await Sync(a, false);

        await Put(a, "t.isolated", new { from = "a" });

        Assert.True((await Prefs(a)).TryGetProperty("t.isolated", out _));
        Assert.False((await Prefs(b)).TryGetProperty("t.isolated", out _));
    }

    [Fact]
    public async Task Enabling_sync_overwrites_every_other_device_from_the_source()
    {
        HttpClient a = Client("dev_enable_src");
        HttpClient b = Client("dev_enable_other");
        await Sync(a, false);

        await Put(a, "t.enable", new { winner = "a" });
        await Put(b, "t.enable", new { winner = "b" });
        await Put(b, "t.enable.only.b", new { keep = false });

        JsonElement state = await Json(await Sync(a, true));
        Assert.True(state.GetProperty("enabled").GetBoolean());
        Assert.Equal("dev_enable_src", state.GetProperty("sourceDevice").GetString());

        // Both devices now READ the synced record, which is the source's set.
        foreach (HttpClient c in new[] { a, b })
            Assert.Equal("a", (await Prefs(c)).GetProperty("t.enable").GetProperty("value")
                .GetProperty("winner").GetString());

        // And the other device's OWN rows were overwritten from the source — the fact a read cannot show
        // while the switch is on, and the reason turning it off later hands back something somebody chose.
        IReadOnlyList<PreferenceRow> other = await Slot("dev_enable_other");
        Assert.Equal("a", JsonDocument.Parse(other.First(r => r.Key == "t.enable").Value)
            .RootElement.GetProperty("winner").GetString());
        Assert.DoesNotContain(other, r => r.Key == "t.enable.only.b");

        await Sync(a, false);
    }

    [Fact]
    public async Task While_sync_is_on_a_write_from_any_device_reaches_all_of_them()
    {
        HttpClient a = Client("dev_shared_a");
        HttpClient b = Client("dev_shared_b");
        await Sync(a, false);
        await Put(a, "t.shared", new { v = 0 });
        await Sync(a, true);

        await Put(b, "t.shared", new { v = 1 });

        Assert.Equal(1, (await Prefs(a)).GetProperty("t.shared").GetProperty("value").GetProperty("v").GetInt32());
        // The write landed on the synced record but is still attributed to the machine that made it —
        // the tiebreak a peer needs when two nodes reach the same version.
        Assert.Equal("dev_shared_b",
            (await Prefs(a)).GetProperty("t.shared").GetProperty("originDevice").GetString());

        await Sync(a, false);
    }

    [Fact]
    public async Task Disabling_sync_seeds_every_known_device_from_the_synced_record()
    {
        HttpClient a = Client("dev_disable_src");
        HttpClient b = Client("dev_disable_other");
        await Sync(a, false);

        // b exists (it has written something), a is the source.
        await Put(b, "t.disable.b", new { from = "b" });
        await Put(a, "t.disable", new { from = "a" });
        await Sync(a, true);

        JsonElement off = await Json(await Sync(a, false));
        Assert.False(off.GetProperty("enabled").GetBoolean());
        // Nothing is switched on, so no device is the source — reported absent rather than left stale.
        Assert.True(off.GetProperty("sourceDevice").ValueKind == JsonValueKind.Null);

        // Both devices kept the synced set instead of falling back to whatever they held before.
        foreach (HttpClient c in new[] { a, b })
            Assert.Equal("a", (await Prefs(c)).GetProperty("t.disable").GetProperty("value")
                .GetProperty("from").GetString());
    }

    [Fact]
    public async Task A_device_that_has_never_written_is_seeded_when_sync_goes_off()
    {
        HttpClient a = Client("dev_seed_src");
        await Sync(a, false);
        await Put(a, "t.seed", new { layout = "wide" });
        await Sync(a, true);

        // A browser that only ever read is not a "known device" — it owns no rows. It still must not land
        // on an empty dashboard, so the disable seeds the caller too.
        HttpClient fresh = Client("dev_seed_fresh");
        await Sync(fresh, false);

        Assert.Equal("wide", (await Prefs(fresh)).GetProperty("t.seed").GetProperty("value")
            .GetProperty("layout").GetString());
    }

    [Fact]
    public async Task The_switch_reads_back_without_a_device()
    {
        HttpClient none = Client(device: null);
        HttpResponseMessage resp = await none.GetAsync("/api/v1/me/preferences/sync");
        // Account-scoped: whether preferences follow the person says nothing about which machine asked.
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True((await Json(resp)).TryGetProperty("enabled", out _));
    }

    [Fact]
    public async Task A_device_scoped_call_without_the_header_is_refused_by_name()
    {
        HttpClient none = Client(device: null);

        foreach (HttpResponseMessage resp in new[]
        {
            await none.GetAsync("/api/v1/me/preferences"),
            await Put(none, "t.headerless", 1),
            await Sync(none, false),
        })
        {
            // Refused, not defaulted: the empty device is the synced record's own slot, and writing there
            // silently would publish one machine's arrangement to every device on the account.
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            Assert.Equal("device_required", (await Json(resp)).GetProperty("error").GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task A_blank_or_malformed_device_is_refused_too()
    {
        HttpClient blank = Client("   ");
        Assert.Equal(HttpStatusCode.BadRequest, (await blank.GetAsync("/api/v1/me/preferences")).StatusCode);

        HttpClient odd = Client("dev/../other");
        JsonElement body = await Json(await odd.GetAsync("/api/v1/me/preferences"));
        Assert.Equal("bad_request", body.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task An_oversized_value_is_refused_rather_than_stored()
    {
        HttpClient c = Client("dev_big");
        await Sync(c, false);

        HttpResponseMessage resp = await Put(c, "t.big", new { blob = new string('x', 70 * 1024) });
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, resp.StatusCode);
        Assert.Equal("value_too_large", (await Json(resp)).GetProperty("error").GetProperty("code").GetString());

        Assert.False((await Prefs(c)).TryGetProperty("t.big", out _));
    }

    [Fact]
    public async Task A_write_needs_a_value_and_a_usable_key()
    {
        HttpClient c = Client("dev_shape");
        await Sync(c, false);

        HttpResponseMessage noValue = await c.PutAsJsonAsync("/api/v1/me/preferences/t.novalue", new { });
        Assert.Equal(HttpStatusCode.BadRequest, noValue.StatusCode);

        HttpResponseMessage badKey = await Put(c, "t space", 1);
        Assert.Equal(HttpStatusCode.BadRequest, badKey.StatusCode);
    }

    [Fact]
    public async Task Preferences_need_a_session()
    {
        HttpClient anon = factory.CreateClient();
        anon.DefaultRequestHeaders.Add(DeviceHeader, "dev_anon");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/me/preferences")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/me/preferences/sync")).StatusCode);
    }

    [Fact]
    public async Task Somebody_waiting_on_an_admin_still_owns_their_own_preferences()
    {
        // Tier `none` — an account awaiting approval. Their own settings are self-service, so the gate is
        // [Authorize] and not a tier: being unable to see the fleet is not a reason to be unable to
        // arrange your own panel.
        HttpClient c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", factory.AccessToken(KgsmTier.None));
        c.DefaultRequestHeaders.Add(DeviceHeader, "dev_pending");

        Assert.Equal(HttpStatusCode.OK, (await Put(c, "t.pending", new { ok = true })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/me/preferences")).StatusCode);
    }
}
