using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;

using TheKrystalShip.Api.Services.Auth;

using TheKrystalShip.KGSM.Auth;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// The M7 assistant turn relay (<c>POST /api/v1/assistant/turn</c>). The factory leaves the assistant
/// UNPROVISIONED (no base URL), so these prove the gates that run BEFORE any upstream call — auth, the
/// honest degrade-gracefully capability gate (absent ⇒ 404, never a 500), and prompt validation. The
/// happy-path stream (a live/stub assistant) is a smoke/live concern, like M2/M3's streaming halves.
/// </summary>
public sealed class AssistantRelayTests(AuthTestFactory factory) : IClassFixture<AuthTestFactory>
{
    private HttpClient Client(string? token = null)
    {
        HttpClient c = factory.CreateClient();
        if (token is not null)
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private static HttpRequestMessage Turn(string json) =>
        new(HttpMethod.Post, "/api/v1/assistant/turn")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    [Fact]
    public async Task NoToken_401()
    {
        HttpResponseMessage resp = await Client().SendAsync(Turn("""{"prompt":"hi"}"""));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Contains("\"code\":\"unauthorized\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task NoneTier_403()
    {
        // Authenticated but below viewer → forbidden (the load-bearing 401-vs-403 split).
        HttpResponseMessage resp = await Client(factory.AccessToken(KgsmTier.None)).SendAsync(Turn("""{"prompt":"hi"}"""));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Viewer_AssistantAbsent_404()
    {
        // Viewer clears authz; the unprovisioned assistant degrades to an honest 404, never a 500.
        HttpResponseMessage resp = await Client(factory.AccessToken(KgsmTier.Viewer)).SendAsync(Turn("""{"prompt":"hi"}"""));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Contains("\"code\":\"not_found\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Viewer_EmptyPrompt_400()
    {
        // Prompt validation precedes the capability gate — a whitespace prompt is a 400 envelope.
        HttpResponseMessage resp = await Client(factory.AccessToken(KgsmTier.Viewer)).SendAsync(Turn("""{"prompt":"   "}"""));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("\"code\":\"bad_request\"", await resp.Content.ReadAsStringAsync());
    }

    // --- POST /confirm — the OPERATOR-gated finalize relay (it executes a mutation, unlike the turn). ---

    private static HttpRequestMessage Confirm(string json) =>
        new(HttpMethod.Post, "/api/v1/assistant/confirm")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    [Fact]
    public async Task Confirm_NoToken_401()
    {
        HttpResponseMessage resp = await Client().SendAsync(Confirm("""{"token":"t"}"""));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Contains("\"code\":\"unauthorized\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Confirm_ViewerTier_403()
    {
        // The load-bearing new gate: confirm EXECUTES a mutation, so it is operator-gated — a viewer
        // (who may chat + propose via the turn) is forbidden here, 403 before any capability/upstream check.
        HttpResponseMessage resp = await Client(factory.AccessToken(KgsmTier.Viewer)).SendAsync(Confirm("""{"token":"t"}"""));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Confirm_Operator_MissingToken_400()
    {
        // Token validation precedes the capability gate — a blank token is a 400 envelope, like the turn's prompt.
        HttpResponseMessage resp = await Client(factory.AccessToken(KgsmTier.Operator)).SendAsync(Confirm("""{"token":"   "}"""));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("\"code\":\"bad_request\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Confirm_Operator_AssistantAbsent_404()
    {
        // Operator clears authz; the unprovisioned assistant degrades to an honest 404, never a 500.
        HttpResponseMessage resp = await Client(factory.AccessToken(KgsmTier.Operator)).SendAsync(Confirm("""{"token":"t"}"""));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Contains("\"code\":\"not_found\"", await resp.Content.ReadAsStringAsync());
    }

    // --- POST /conversations/{id}/compact — same relay gates as the reads/delete (viewer, degrade). ---

    private static HttpRequestMessage Compact(string id) =>
        new(HttpMethod.Post, $"/api/v1/assistant/conversations/{id}/compact");

    [Fact]
    public async Task Compact_NoToken_401()
    {
        HttpResponseMessage resp = await Client().SendAsync(Compact("chat1"));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Contains("\"code\":\"unauthorized\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Compact_NoneTier_403()
    {
        // Authenticated but below viewer → forbidden (same gate as the turn/reads).
        HttpResponseMessage resp = await Client(factory.AccessToken(KgsmTier.None)).SendAsync(Compact("chat1"));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Compact_Viewer_AssistantAbsent_404()
    {
        // Viewer clears authz; the unprovisioned assistant degrades to an honest 404, never a 500.
        HttpResponseMessage resp = await Client(factory.AccessToken(KgsmTier.Viewer)).SendAsync(Compact("chat1"));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Contains("\"code\":\"not_found\"", await resp.Content.ReadAsStringAsync());
    }

    // --- GET /admin/conversations… — the ADMIN-gated review relay (it reads OTHER users' chats). ---
    // Every conversation endpoint above reads the CALLER'S OWN history and is viewer-gated. These read
    // someone else's, so the tier is the load-bearing difference and is what these tests pin: a viewer or
    // an operator — either of whom may chat, and an operator may even propose commands — is forbidden.

    [Theory]
    [InlineData("/api/v1/assistant/admin/conversations/users")]
    [InlineData("/api/v1/assistant/admin/conversations/stats")]
    [InlineData("/api/v1/assistant/admin/conversations?user=u1")]
    [InlineData("/api/v1/assistant/admin/conversations/aGFuZGxl")]
    public async Task Review_NoToken_401(string path)
    {
        HttpResponseMessage resp = await Client().GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Contains("\"code\":\"unauthorized\"", await resp.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/api/v1/assistant/admin/conversations/users")]
    [InlineData("/api/v1/assistant/admin/conversations/stats")]
    [InlineData("/api/v1/assistant/admin/conversations?user=u1")]
    [InlineData("/api/v1/assistant/admin/conversations/aGFuZGxl")]
    public async Task Review_ViewerTier_403(string path)
    {
        // A viewer may chat with the assistant and read their OWN history — never anyone else's.
        HttpResponseMessage resp = await Client(factory.AccessToken(KgsmTier.Viewer)).GetAsync(path);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/assistant/admin/conversations/users")]
    [InlineData("/api/v1/assistant/admin/conversations/stats")]
    [InlineData("/api/v1/assistant/admin/conversations?user=u1")]
    [InlineData("/api/v1/assistant/admin/conversations/aGFuZGxl")]
    public async Task Review_OperatorTier_403(string path)
    {
        // Operator is the tier that may ACT on a server. Reading someone's conversation is a different
        // power, and this is the assertion that keeps the two from being conflated later.
        HttpResponseMessage resp = await Client(factory.AccessToken(KgsmTier.Operator)).GetAsync(path);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/assistant/admin/conversations/users")]
    [InlineData("/api/v1/assistant/admin/conversations/stats")]
    [InlineData("/api/v1/assistant/admin/conversations?user=u1")]
    [InlineData("/api/v1/assistant/admin/conversations/aGFuZGxl")]
    public async Task Review_Admin_AssistantAbsent_404(string path)
    {
        // Admin clears authz; the unprovisioned assistant degrades to an honest 404, never a 500.
        HttpResponseMessage resp = await Client(factory.AccessToken(KgsmTier.Admin)).GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Contains("\"code\":\"not_found\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Review_Admin_UnknownConversation_IsNotFound_NotBadGateway()
    {
        // A handle the leaf does not hold is an ANSWER ("no such conversation"), not a transport failure.
        // Without the pass-through the client is told the assistant could not be reached — false, and it
        // sends them looking for an outage instead of a stale link. Asserted here at the shape level: with
        // no assistant provisioned the degrade 404 arrives instead, but both are 404 + not_found, which is
        // exactly the contract this pins — this endpoint never answers a missing conversation with a 5xx.
        HttpResponseMessage resp = await Client(factory.AccessToken(KgsmTier.Admin))
            .GetAsync("/api/v1/assistant/admin/conversations/bm9wZQ");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Contains("\"code\":\"not_found\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ReviewStats_IsNotShadowedByTheTranscriptRoute()
    {
        // "stats" is a literal segment sharing a template shape with admin/conversations/{handle}. If the
        // parameter route ever won, this would be relayed as a conversation handle and quietly 404 — so the
        // assertion is that an ADMIN reaches the capability gate (404 "not_found" from the unprovisioned
        // assistant) rather than being rejected as a bad handle by some other path.
        HttpResponseMessage resp = await Client(factory.AccessToken(KgsmTier.Admin))
            .GetAsync("/api/v1/assistant/admin/conversations/stats");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Contains("\"code\":\"not_found\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Review_Admin_MissingUser_400()
    {
        // The user to review is required, and validating it precedes the capability gate — same shape as
        // the turn's prompt and the confirm's token.
        HttpResponseMessage resp = await Client(factory.AccessToken(KgsmTier.Admin))
            .GetAsync("/api/v1/assistant/admin/conversations");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("\"code\":\"bad_request\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Feedback_ViewerTier_ReachesTheCapabilityGate()
    {
        // Rating the answer YOU received is a personal action on your own conversation, like reading or
        // deleting it — so viewer must reach it. If this ever starts 403ing, the endpoint has been
        // conflated with the admin review surface next door, which reads OTHER people's conversations.
        HttpResponseMessage resp = await Client(factory.AccessToken(KgsmTier.Viewer))
            .PostAsJsonAsync("/api/v1/assistant/conversations/chatA/turns/42/feedback",
                new { rating = "down", note = "wrong port" });

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);   // the unprovisioned assistant degrades
        Assert.Contains("\"code\":\"not_found\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Feedback_Unauthenticated_401()
    {
        HttpResponseMessage resp = await factory.CreateClient()
            .PostAsJsonAsync("/api/v1/assistant/conversations/chatA/turns/42/feedback", new { rating = "up" });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Feedback_TurnIdMustBeANumber()
    {
        // The route constrains turnId to a long. A non-numeric segment must not fall through to some other
        // template and be read as part of a conversation key.
        HttpResponseMessage resp = await Client(factory.AccessToken(KgsmTier.Viewer))
            .PostAsJsonAsync("/api/v1/assistant/conversations/chatA/turns/not-a-turn/feedback",
                new { rating = "up" });

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // --- /memories — what the assistant has written down about the caller. Viewer-gated on BOTH verbs:
    // reading and pruning your own memory is a personal read-surface action, exactly like your own
    // conversations, so the delete deliberately does NOT climb to operator the way /confirm does. ---

    [Fact]
    public async Task Memories_NoToken_401()
    {
        HttpResponseMessage resp = await Client().GetAsync("/api/v1/assistant/memories");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Contains("\"code\":\"unauthorized\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Memories_NoneTier_403()
    {
        HttpResponseMessage resp = await Client(factory.AccessToken(KgsmTier.None))
            .GetAsync("/api/v1/assistant/memories");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Memories_Viewer_AssistantAbsent_404()
    {
        // The capability gate: an unprovisioned assistant degrades to an honest 404, never a 500 and
        // never an empty list — "nothing is remembered" and "there is no assistant" are different answers.
        HttpResponseMessage resp = await Client(factory.AccessToken(KgsmTier.Viewer))
            .GetAsync("/api/v1/assistant/memories");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Contains("\"code\":\"not_found\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task DeleteMemory_NoToken_401()
    {
        HttpResponseMessage resp = await Client().DeleteAsync("/api/v1/assistant/memories/some-key");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task DeleteMemory_NoneTier_403()
    {
        HttpResponseMessage resp = await Client(factory.AccessToken(KgsmTier.None))
            .DeleteAsync("/api/v1/assistant/memories/some-key");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task DeleteMemory_Viewer_AssistantAbsent_404()
    {
        HttpResponseMessage resp = await Client(factory.AccessToken(KgsmTier.Viewer))
            .DeleteAsync("/api/v1/assistant/memories/some-key");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Contains("\"code\":\"not_found\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task DeleteMemory_KeyIsEscapedIntoTheRelayPath()
    {
        // A key travels in the path, so it is escaped rather than concatenated. A slash in one must not
        // be able to address a different upstream route; it still reaches the same capability gate.
        HttpResponseMessage resp = await Client(factory.AccessToken(KgsmTier.Viewer))
            .DeleteAsync("/api/v1/assistant/memories/" + Uri.EscapeDataString("a/../confirm"));

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Contains("\"code\":\"not_found\"", await resp.Content.ReadAsStringAsync());
    }

    // --- writing one by hand. Viewer-gated with the two beside it, for the same reason: correcting
    // what is remembered about YOU is a personal action, not authority over a host. ---

    [Fact]
    public async Task WriteMemory_NoToken_401()
    {
        HttpResponseMessage resp = await Client()
            .PutAsJsonAsync("/api/v1/assistant/memories/some-key", new { summary = "Something." });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task WriteMemory_NoneTier_403()
    {
        HttpResponseMessage resp = await Client(factory.AccessToken(KgsmTier.None))
            .PutAsJsonAsync("/api/v1/assistant/memories/some-key", new { summary = "Something." });
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task WriteMemory_Viewer_AssistantAbsent_404()
    {
        HttpResponseMessage resp = await Client(factory.AccessToken(KgsmTier.Viewer))
            .PutAsJsonAsync("/api/v1/assistant/memories/some-key", new { summary = "Something." });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Contains("\"code\":\"not_found\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task WriteMemory_KeyIsEscapedIntoTheRelayPath()
    {
        HttpResponseMessage resp = await Client(factory.AccessToken(KgsmTier.Viewer))
            .PutAsJsonAsync(
                "/api/v1/assistant/memories/" + Uri.EscapeDataString("a/../confirm"),
                new { summary = "Something." });

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Contains("\"code\":\"not_found\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task MemoryLimits_NoToken_401()
    {
        HttpResponseMessage resp = await Client().GetAsync("/api/v1/assistant/memories/limits");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task MemoryLimits_Viewer_AssistantAbsent_404()
    {
        // `limits` is a literal segment sharing a template shape with `{key}`. Routing must send this
        // to the limits action rather than reading it as somebody's memory named "limits" — an absent
        // assistant answers 404 either way, so the claim worth holding is that the GET resolves at all.
        HttpResponseMessage resp = await Client(factory.AccessToken(KgsmTier.Viewer))
            .GetAsync("/api/v1/assistant/memories/limits");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Contains("\"code\":\"not_found\"", await resp.Content.ReadAsStringAsync());
    }
}
