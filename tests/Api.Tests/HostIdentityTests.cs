using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Auth;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// The host identity card — the runtime-derived block on <c>GET /hosts</c> (<c>identity.os/runtime/build/
/// startedAt</c>) + the operator-editable <c>region</c>/<c>label</c> overrides via <c>PATCH /hosts/{id}</c>,
/// and the public identity fields on the open <c>GET /api/v1</c> handshake. Load-bearing assertions:
/// <list type="bullet">
///   <item>the build version is the assembly's REAL informational version (never a fabricated semver);</item>
///   <item>region is honest-unknown (present-as-null on the host, omitted on the handshake) until declared;</item>
///   <item>the edit is admin-only (the 401/403 split) and config seeds the default while an override wins.</item>
/// </list>
/// Fresh factory per test (the IntegrationsApiTests pattern): each gets an isolated DB + store, so a PATCH
/// in one test can't bleed into another.
/// </summary>
public sealed class HostIdentityTests
{
    private static readonly string ExpectedBuild =
        typeof(ApiInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

    // --- the identity card on GET /hosts -----------------------------------------------------------

    [Fact]
    public async Task Host_Identity_RuntimeFields_Present_OnListAndDetail()
    {
        using var f = new AuthTestFactory();
        HttpClient c = Client(f, AuthTier.Viewer);

        using JsonDocument list = await GetJson(c, "/api/v1/hosts");
        AssertIdentityShape(list.RootElement.EnumerateArray().Single().GetProperty("identity"));

        // Detail is a superset of the list — the identity rides both.
        using JsonDocument detail = await GetJson(c, $"/api/v1/hosts/{AuthTestFactory.HostId}");
        AssertIdentityShape(detail.RootElement.GetProperty("identity"));
    }

    private static void AssertIdentityShape(JsonElement identity)
    {
        // Build = the assembly's real informational version (<Version> + git SHA), NOT a fabricated value.
        Assert.Equal(ExpectedBuild, identity.GetProperty("build").GetString());
        Assert.False(string.IsNullOrWhiteSpace(identity.GetProperty("build").GetString()));

        // Runtime is always sourced (".NET …").
        Assert.False(string.IsNullOrWhiteSpace(identity.GetProperty("runtime").GetString()));

        // startedAt is a real, parseable UTC instant (the 'Z' convention), not a placeholder.
        string startedAt = identity.GetProperty("startedAt").GetString()!;
        Assert.EndsWith("Z", startedAt);
        Assert.True(DateTimeOffset.TryParse(startedAt, out _));

        // os.arch is always available from the runtime; name/kernel are present (string-or-null, honest).
        JsonElement os = identity.GetProperty("os");
        Assert.False(string.IsNullOrWhiteSpace(os.GetProperty("arch").GetString()));
        Assert.True(os.TryGetProperty("name", out _));
        Assert.True(os.TryGetProperty("kernel", out _));

        // region: honest-unknown — present as explicit null when neither override nor config is set.
        Assert.Equal(JsonValueKind.Null, identity.GetProperty("region").ValueKind);
    }

    // --- the open handshake (GET /api/v1) ----------------------------------------------------------

    [Fact]
    public async Task Handshake_CarriesBuildAndLabel_RegionOmittedWhenUnset()
    {
        using var f = new AuthTestFactory();
        using JsonDocument root = await GetJson(f.CreateClient(), "/api/v1");   // open — no token
        JsonElement info = root.RootElement;

        Assert.Equal(ExpectedBuild, info.GetProperty("build").GetString());
        // Label defaults to the host id when KGSM_API_HOST_LABEL is unset.
        Assert.Equal(AuthTestFactory.HostId, info.GetProperty("label").GetString());
        // The route version is still "v1" (a separate axis from the build version).
        Assert.Equal(ApiInfo.ApiVersion, info.GetProperty("version").GetString());
        // Region unset => OMITTED on the handshake (JsonIgnore WhenWritingNull), never a guessed value.
        Assert.False(info.TryGetProperty("region", out _));
    }

    [Fact]
    public async Task Handshake_PanelVersionAxisUnchanged_BuildIsTheNewAxis()
    {
        // The host's panelVersion (route version) and the handshake version still agree; build is additive.
        using var f = new AuthTestFactory();
        using JsonDocument root = await GetJson(f.CreateClient(), "/api/v1");
        using JsonDocument hosts = await GetJson(Client(f, AuthTier.Viewer), "/api/v1/hosts");

        string handshakeVersion = root.RootElement.GetProperty("version").GetString()!;
        JsonElement host = hosts.RootElement.EnumerateArray().Single();
        Assert.Equal(handshakeVersion, host.GetProperty("panelVersion").GetString());
        // The build version is reported identically on both surfaces.
        Assert.Equal(root.RootElement.GetProperty("build").GetString(),
            host.GetProperty("identity").GetProperty("build").GetString());
    }

    // --- PATCH /hosts/{id} — the edit path ---------------------------------------------------------

    [Fact]
    public async Task Patch_Admin_SetsRegionAndLabel_ReflectedEverywhere()
    {
        using var f = new AuthTestFactory();
        HttpClient admin = Client(f, AuthTier.Admin);

        HttpResponseMessage patch = await admin.PatchAsJsonAsync(
            $"/api/v1/hosts/{AuthTestFactory.HostId}", new { region = "eu-west", label = "Hotrod" });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        // The PATCH response is the refreshed host detail — already shows the new values.
        JsonElement patched = JsonDocument.Parse(await patch.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("Hotrod", patched.GetProperty("label").GetString());
        Assert.Equal("eu-west", patched.GetProperty("identity").GetProperty("region").GetString());

        // And it persists / is visible to a fresh viewer read.
        using JsonDocument list = await GetJson(Client(f, AuthTier.Viewer), "/api/v1/hosts");
        JsonElement host = list.RootElement.EnumerateArray().Single();
        Assert.Equal("Hotrod", host.GetProperty("label").GetString());
        Assert.Equal("eu-west", host.GetProperty("identity").GetProperty("region").GetString());

        // The open handshake now carries the region too (no longer omitted).
        using JsonDocument root = await GetJson(f.CreateClient(), "/api/v1");
        Assert.Equal("eu-west", root.RootElement.GetProperty("region").GetString());
        Assert.Equal("Hotrod", root.RootElement.GetProperty("label").GetString());
    }

    [Fact]
    public async Task Patch_Sparse_OnlyPresentFieldsChange()
    {
        using var f = new AuthTestFactory();
        HttpClient admin = Client(f, AuthTier.Admin);

        await admin.PatchAsJsonAsync($"/api/v1/hosts/{AuthTestFactory.HostId}", new { region = "us-east", label = "Box" });
        // A second patch touching only region must leave the label intact.
        HttpResponseMessage second = await admin.PatchAsJsonAsync(
            $"/api/v1/hosts/{AuthTestFactory.HostId}", new { region = "us-west" });
        JsonElement host = JsonDocument.Parse(await second.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("us-west", host.GetProperty("identity").GetProperty("region").GetString());
        Assert.Equal("Box", host.GetProperty("label").GetString());   // unchanged
    }

    [Fact]
    public async Task Patch_BlankString_ClearsOverride_BackToConfigDefault()
    {
        using var f = new AuthTestFactory();
        HttpClient admin = Client(f, AuthTier.Admin);

        await admin.PatchAsJsonAsync($"/api/v1/hosts/{AuthTestFactory.HostId}", new { region = "eu-west", label = "Hotrod" });
        // Empty string clears: region -> null (no config default), label -> the host id (config default).
        HttpResponseMessage cleared = await admin.PatchAsJsonAsync(
            $"/api/v1/hosts/{AuthTestFactory.HostId}", new { region = "", label = "" });
        JsonElement host = JsonDocument.Parse(await cleared.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(JsonValueKind.Null, host.GetProperty("identity").GetProperty("region").ValueKind);
        Assert.Equal(AuthTestFactory.HostId, host.GetProperty("label").GetString());
    }

    [Fact]
    public async Task Patch_NoToken_401()
    {
        using var f = new AuthTestFactory();
        HttpResponseMessage r = await f.CreateClient().PatchAsJsonAsync(
            $"/api/v1/hosts/{AuthTestFactory.HostId}", new { region = "eu-west" });
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Contains("\"code\":\"unauthorized\"", await r.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(AuthTier.Viewer)]
    [InlineData(AuthTier.Operator)]
    public async Task Patch_BelowAdmin_403(AuthTier tier)
    {
        using var f = new AuthTestFactory();
        HttpResponseMessage r = await Client(f, tier).PatchAsJsonAsync(
            $"/api/v1/hosts/{AuthTestFactory.HostId}", new { region = "eu-west" });
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task Patch_UnknownHostId_404()
    {
        using var f = new AuthTestFactory();
        HttpResponseMessage r = await Client(f, AuthTier.Admin).PatchAsJsonAsync(
            "/api/v1/hosts/not-this-host", new { region = "eu-west" });
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Contains("\"code\":\"not_found\"", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Patch_OverLength_400_Envelope()
    {
        using var f = new AuthTestFactory();
        string tooLong = new('x', 101);   // MaxIdentityLength is 100
        HttpResponseMessage r = await Client(f, AuthTier.Admin).PatchAsJsonAsync(
            $"/api/v1/hosts/{AuthTestFactory.HostId}", new { region = tooLong });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("\"code\":\"bad_request\"", await r.Content.ReadAsStringAsync());
    }

    // --- config seeds the default ------------------------------------------------------------------

    [Fact]
    public async Task Region_ConfigDefault_SurfacesUntilOverridden()
    {
        using var f = new RegionConfiguredFactory();
        // The configured KGSM_API_REGION is the default on both the host card and the handshake.
        using JsonDocument list = await GetJson(Client(f, AuthTier.Viewer), "/api/v1/hosts");
        Assert.Equal("configured-region",
            list.RootElement.EnumerateArray().Single().GetProperty("identity").GetProperty("region").GetString());
        using JsonDocument root = await GetJson(f.CreateClient(), "/api/v1");
        Assert.Equal("configured-region", root.RootElement.GetProperty("region").GetString());

        // An override wins over the config default.
        await Client(f, AuthTier.Admin).PatchAsJsonAsync(
            $"/api/v1/hosts/{AuthTestFactory.HostId}", new { region = "override-region" });
        using JsonDocument after = await GetJson(Client(f, AuthTier.Viewer), "/api/v1/hosts");
        Assert.Equal("override-region",
            after.RootElement.EnumerateArray().Single().GetProperty("identity").GetProperty("region").GetString());
    }

    // --- helpers -----------------------------------------------------------------------------------

    private static HttpClient Client(AuthTestFactory f, AuthTier tier)
    {
        HttpClient c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", f.AccessToken(tier));
        return c;
    }

    private static async Task<JsonDocument> GetJson(HttpClient c, string path)
    {
        HttpResponseMessage resp = await c.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
    }

    /// <summary>An <see cref="AuthTestFactory"/> with KGSM_API_REGION configured — proves config seeds the
    /// default region (before any override).</summary>
    private sealed class RegionConfiguredFactory : AuthTestFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["KGSM_API_REGION"] = "configured-region" }));
        }
    }
}
