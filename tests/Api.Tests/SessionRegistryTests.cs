using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.Api.Services.Auth;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// M4·c Increment 3 — the session registry write path, end-to-end through the real OAuth callback.
/// A real login (FakeDiscordResolver: code=operator) mints a sid-bearing token pair AND inserts a
/// <see cref="SessionEntry"/> row (the registry the per-request validator, landing in Increment 4,
/// reads to decide "is this session still alive"). The audit <c>auth.login</c> row's <c>meta.sid</c>
/// links the two — forensics from a login event forward to its session row (and later, its revoke).
/// <para>
/// These run against the shared per-class <see cref="AuthTestFactory"/> DB (the same DB the tier matrix
/// + audit tests use). Each callback mints a unique <c>sid_&lt;guid&gt;</c>, so the assertions are
/// scoped to that sid — no cross-test contamination.
/// </para>
/// </summary>
public sealed class SessionRegistryTests(AuthTestFactory factory) : IClassFixture<AuthTestFactory>
{
    private static async Task<JsonElement> Json(HttpResponseMessage resp) =>
        JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;

    // Base64url-decode a JWT's payload (no signing key needed — read-only inspection of the `sid`
    // claim). The SPA will do the same client-side for its token store; the test gets to assert the
    // claim is actually present + matches the session row.
    private static string? SidFromToken(string jwt)
    {
        string payload = jwt.Split('.')[1];
        // Pad to a multiple of 4 for base64url → base64 conversion.
        payload = payload.Replace('-', '+').Replace('_', '/').PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        byte[] bytes = Convert.FromBase64String(payload);
        using JsonDocument doc = JsonDocument.Parse(bytes);
        return doc.RootElement.TryGetProperty("sid", out JsonElement sid) && sid.ValueKind == JsonValueKind.String
            ? sid.GetString()
            : null;
    }

    // Drive a real callback through the CSRF round-trip (the same helper shape AuthFlowTests uses).
    private async Task<(HttpClient Client, string State)> BeginLogin()
    {
        HttpClient c = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        HttpResponseMessage start = await c.GetAsync("/auth/discord/start");
        string query = start.Headers.Location!.Query.TrimStart('?');
        string state = query.Split('&').First(kv => kv.StartsWith("state=")).Substring("state=".Length);
        return (c, state);
    }

    [Fact]
    public async Task Login_MintsSidBearingToken_AndInsertsSessionRow()
    {
        (HttpClient c, string state) = await BeginLogin();
        HttpResponseMessage resp = await c.GetAsync($"/auth/discord/callback?code=operator&state={state}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        JsonElement body = await Json(resp);
        Assert.Equal("ok", body.GetProperty("verdict").GetString());

        // The minted access token carries the `sid` claim — decode it (no signing key needed).
        string access = body.GetProperty("token").GetString()!;
        string? sid = SidFromToken(access);
        Assert.NotNull(sid);
        Assert.StartsWith("sid_", sid);

        // The session row lands: the registry IS the authority the per-request validator reads.
        // AppDbContext is scoped (EF default) — resolve it through a scope off the root provider.
        using IServiceScope scope = factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        SessionEntry? row = await db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sid);
        Assert.NotNull(row);
        Assert.Equal("discord:198772043", row!.UserId);   // FakeDiscordResolver.Identity
        Assert.Equal(AuthTestFactory.HostId, row.HostId);
        Assert.False(row.Revoked);
        Assert.Null(row.RevokedAt);
        // Expires ≈ now + 30d (SessionsRefreshAbsoluteDays default). Allow a 5s skew for the test's
        // own latency between mint and read; the absolute scale is what matters.
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Assert.InRange(row.Expires, now + TimeSpan.FromDays(30) - TimeSpan.FromSeconds(5),
            now + TimeSpan.FromDays(30) + TimeSpan.FromSeconds(5));
        // Created == LastSeen at login (LastSeen advances on refresh — Increment 5).
        Assert.Equal(row.Created, row.LastSeen);
        // UserAgent: the WebApplicationFactory's HttpClient sends no User-Agent by default → null
        // (the controller normalizes a blank UA to null). A real browser would carry one.
        Assert.Null(row.UserAgent);
    }

    [Fact]
    public async Task Login_AuditMetaCarriesSid_LinkingToSessionRow()
    {
        (HttpClient c, string state) = await BeginLogin();
        HttpResponseMessage resp = await c.GetAsync($"/auth/discord/callback?code=operator&state={state}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        string sid = SidFromToken((await Json(resp)).GetProperty("token").GetString()!)!;

        // The auth.login audit row's meta.sid links it to the session row just created.
        HttpClient viewer = factory.CreateClient();
        viewer.DefaultRequestHeaders.Authorization = new("Bearer", factory.AccessToken(AuthTier.Viewer));
        JsonElement body = await Json(await viewer.GetAsync("/api/v1/audit?actor=haru"));
        JsonElement[] rows = body.GetProperty("data").EnumerateArray().ToArray();
        JsonElement loginRow = rows.First(x => x.GetProperty("action").GetString() == AuditAction.AuthLogin
            && x.GetProperty("meta").TryGetProperty("sid", out JsonElement s) && s.GetString() == sid);
        Assert.Equal(sid, loginRow.GetProperty("meta").GetProperty("sid").GetString());
        // The meta still carries the tier (the M5 field — M4·c only adds sid alongside it).
        Assert.Equal("operator", loginRow.GetProperty("meta").GetProperty("tier").GetString());
    }
}