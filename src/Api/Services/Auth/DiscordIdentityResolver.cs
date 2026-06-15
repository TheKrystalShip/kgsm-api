using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace TheKrystalShip.Api.Services.Auth;

/// <summary>Raised when Discord cannot be reached or answers unexpectedly during the OAuth
/// exchange / role lookup. The caller maps it to a <c>502</c> — an honest "auth provider error",
/// <strong>never</strong> a default grant (the security analog of never-fabricate-a-status).</summary>
public sealed class DiscordAuthException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// The real Discord seam (M4·a built; M4·b live-validated). Talks to <c>discord.com</c> directly
/// over HTTP: exchanges the OAuth code for a user token, verifies identity via <c>/users/@me</c>,
/// then reads the member's guild roles with the <strong>bot token</strong> — the only path to roles,
/// since the <c>identify guilds</c> user scopes don't carry them (architecture.html:570). The user
/// token is used once and discarded; nothing Discord-side is persisted. Holding the bot token here
/// is SHARED EXTERNAL CONFIG (the same app/guild/role the host's bot uses), not a process dependency
/// on kgsm-bot (keystone §4).
/// </summary>
public sealed class DiscordIdentityResolver(
    HttpClient http,
    ApiOptions options,
    ILogger<DiscordIdentityResolver> logger) : IDiscordIdentityResolver
{
    private const string ApiBase = "https://discord.com/api";
    // The user scopes (architecture.html:570). Roles come from the bot token, not these.
    private const string Scopes = "identify guilds";

    public string BuildAuthorizeUrl(string state, string prompt)
    {
        string p = prompt is "consent" ? "consent" : "none";
        var q = new Dictionary<string, string?>
        {
            ["client_id"] = options.DiscordClientId,
            ["redirect_uri"] = options.DiscordRedirectUri,
            ["response_type"] = "code",
            ["scope"] = Scopes,
            ["state"] = state,
            ["prompt"] = p,
        };
        string query = string.Join('&', q.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value ?? "")}"));
        return $"{ApiBase}/oauth2/authorize?{query}";
    }

    public async Task<ResolvedPrincipal?> ResolveAsync(string code, CancellationToken ct)
    {
        // 1. code -> user access token. A 4xx here means the code is bad/expired (a client problem):
        //    return null -> the caller answers 401/login_required. A 5xx/network error is an upstream
        //    failure -> DiscordAuthException -> 502 (never a grant).
        string? userToken = await ExchangeCodeAsync(code, ct);
        if (userToken is null) return null;

        // 2. verify identity once, then discard the user token (we keep no Discord token).
        DiscordIdentity identity = await FetchIdentityAsync(userToken, ct);

        // 3. resolve the guild role via the BOT token (the only path to roles). 404 = not in guild =
        //    no role here = tier None (a real "verified but unauthorized" verdict, not an error).
        IReadOnlyList<string> roleIds = await FetchGuildRolesAsync(identity.UserId, ct);
        AuthTier tier = AuthTiers.Resolve(roleIds, options);

        logger.LogInformation("Discord auth resolved user {UserId} on guild {GuildId} -> tier {Tier} ({RoleCount} roles)",
            identity.UserId, options.DiscordGuildId, AuthTiers.ToWire(tier), roleIds.Count);
        return new ResolvedPrincipal(identity, tier);
    }

    private async Task<string?> ExchangeCodeAsync(string code, CancellationToken ct)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = options.DiscordClientId,
            ["client_secret"] = options.DiscordClientSecret,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = options.DiscordRedirectUri,
        });

        HttpResponseMessage resp;
        try
        {
            resp = await http.PostAsync($"{ApiBase}/oauth2/token", form, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new DiscordAuthException("Discord token endpoint unreachable.", ex);
        }

        using (resp)
        {
            if (resp.StatusCode is >= HttpStatusCode.InternalServerError)
                throw new DiscordAuthException($"Discord token endpoint returned {(int)resp.StatusCode}.");
            if (!resp.IsSuccessStatusCode)
            {
                // 4xx — the authorization code is invalid/expired/reused. A client-recoverable problem.
                logger.LogInformation("Discord code exchange rejected ({Status}).", (int)resp.StatusCode);
                return null;
            }
            string json = await resp.Content.ReadAsStringAsync(ct);
            using JsonDocument doc = SafeParse(json);
            return doc.RootElement.TryGetProperty("access_token", out JsonElement t) ? t.GetString() : null;
        }
    }

    private async Task<DiscordIdentity> FetchIdentityAsync(string userToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/users/@me");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", userToken);
        JsonElement me = await SendForJsonAsync(req, "users/@me", ct);

        string userId = GetString(me, "id") ?? throw new DiscordAuthException("Discord /users/@me had no id.");
        string username = GetString(me, "username") ?? userId;
        string display = GetString(me, "global_name") ?? username;
        string? avatarHash = GetString(me, "avatar");
        string? avatarUrl = avatarHash is null
            ? null
            : $"https://cdn.discordapp.com/avatars/{userId}/{avatarHash}.png";

        return new DiscordIdentity(userId, username, display, avatarUrl, ["identify", "guilds"]);
    }

    private async Task<IReadOnlyList<string>> FetchGuildRolesAsync(string userId, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"{ApiBase}/guilds/{options.DiscordGuildId}/members/{userId}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bot", options.DiscordBotToken);

        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(req, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new DiscordAuthException("Discord guild-member endpoint unreachable.", ex);
        }

        using (resp)
        {
            // Not a member of this guild -> no role here -> tier None (a real verdict, not an error).
            if (resp.StatusCode is HttpStatusCode.NotFound)
                return [];
            if (!resp.IsSuccessStatusCode)
                throw new DiscordAuthException($"Discord guild-member lookup returned {(int)resp.StatusCode}.");

            string json = await resp.Content.ReadAsStringAsync(ct);
            using JsonDocument doc = SafeParse(json);
            if (!doc.RootElement.TryGetProperty("roles", out JsonElement roles) || roles.ValueKind != JsonValueKind.Array)
                return [];
            return roles.EnumerateArray()
                .Select(r => r.GetString())
                .Where(s => !string.IsNullOrEmpty(s))
                .Select(s => s!)
                .ToList();
        }
    }

    private async Task<JsonElement> SendForJsonAsync(HttpRequestMessage req, string what, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(req, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new DiscordAuthException($"Discord {what} endpoint unreachable.", ex);
        }
        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
                throw new DiscordAuthException($"Discord {what} returned {(int)resp.StatusCode}.");
            string json = await resp.Content.ReadAsStringAsync(ct);
            // Clone so the element outlives the JsonDocument we dispose here.
            using JsonDocument doc = SafeParse(json);
            return doc.RootElement.Clone();
        }
    }

    private static JsonDocument SafeParse(string json)
    {
        try { return JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new DiscordAuthException("Discord returned malformed JSON.", ex); }
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
