using System.Collections.Specialized;
using System.Net;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Leaves;
using TheKrystalShip.Kgsm.Assistant.Relay;
using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Discord;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// What the relay actually puts on the wire, captured from a stub standing in for the assistant.
/// </summary>
/// <remarks>
/// The rest of the relay suite runs with the assistant UNPROVISIONED, which is right for the gates that
/// gate before any upstream call — and means no test ever saw a request leave. The forwarded tier is the
/// assistant's whole authority for a relayed caller, so "which headers were sent" is a security property,
/// not a detail: sending the wrong one, or none, changes what a person can do on the other side.
/// </remarks>
public sealed class AssistantRelayHeaderTests : IAsyncLifetime
{
    private HttpListener _listener = null!;
    private string _baseUrl = null!;
    private Task _serving = null!;

    /// <summary>The headers of the last request the stub received.</summary>
    private NameValueCollection? _received;

    public Task InitializeAsync()
    {
        // Port 0 is not available to HttpListener, so take one the OS is willing to give and retry the
        // rare loss of a race with another listener.
        for (int attempt = 0; ; attempt++)
        {
            int port = Random.Shared.Next(20000, 60000);
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                _listener.Start();
                _baseUrl = $"http://127.0.0.1:{port}";
                break;
            }
            catch (HttpListenerException) when (attempt < 10)
            {
                _listener.Close();
            }
        }

        _serving = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync();
                }
                catch (Exception) // the listener stopped
                {
                    return;
                }

                _received = ctx.Request.Headers;
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync("{}"u8.ToArray());
                ctx.Response.Close();
            }
        });

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _listener.Stop();
        _listener.Close();
        return _serving;
    }

    private AssistantClient Client()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Api:AssistantBaseUrl"] = _baseUrl,
                ["Api:AssistantRelaySecret"] = "the-relay-secret",
            })
            .Build();

        ApiOptions options = ApiOptions.FromConfiguration(config);
        var registry = new LeafRegistry(
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            options,
            NullLogger<LeafRegistry>.Instance);

        return new AssistantClient(options, registry, NullLogger<AssistantClient>.Instance);
    }

    private static RelayPrincipal Caller(KgsmTier tier) => new("198772043", "Haru", tier);

    [Theory]
    [InlineData(KgsmTier.Viewer, "viewer")]
    [InlineData(KgsmTier.Operator, "operator")]
    [InlineData(KgsmTier.Admin, "admin")]
    public async Task TheCallersVerifiedTierIsForwardedVerbatim(KgsmTier tier, string wire)
    {
        using AssistantClient client = Client();

        using HttpResponseMessage? response = await client.GetConversationsAsync(Caller(tier), default);

        Assert.NotNull(response);
        Assert.Equal(wire, _received!["X-Relay-Tier"]);
    }

    [Fact]
    public async Task TheRetiredBooleanHeadersAreGone()
    {
        // Both collapsed into the tier. Leaving one behind would mean two sources for one question, and
        // the assistant reading whichever it happened to check first.
        using AssistantClient client = Client();

        using HttpResponseMessage? response = await client.GetConversationsAsync(Caller(KgsmTier.Admin), default);

        Assert.Null(_received!["X-Relay-Can-Act"]);
        Assert.Null(_received["X-Relay-Admin"]);
    }

    [Fact]
    public async Task EveryRelayCallCarriesTheSecretTheIdentityAndTheTier()
    {
        // One helper writes all three, so a call cannot forward a person without their authority.
        using AssistantClient client = Client();

        using HttpResponseMessage? response = await client.GetReviewUsersAsync(Caller(KgsmTier.Admin), default);

        Assert.Equal("the-relay-secret", _received!["X-Relay-Secret"]);
        Assert.Equal("198772043", _received["X-Relay-User"]);
        Assert.Equal("Haru", _received["X-Relay-User-Name"]);
        Assert.Equal("admin", _received["X-Relay-Tier"]);
    }

    [Fact]
    public async Task EveryRelayCallNamesThisLeaf()
    {
        // The assistant reads this to pick which prompts answer and which origin the turn's actions
        // are recorded under. Absent, it falls back to the assistant's own — correct, and silent, so
        // the header going missing would not otherwise show up as a failure anywhere.
        using AssistantClient client = Client();

        using HttpResponseMessage? response = await client.GetReviewUsersAsync(Caller(KgsmTier.Admin), default);

        Assert.Equal("kgsm-api", _received!["X-Relay-Leaf"]);
    }

    [Fact]
    public async Task AReviewCallForwardsTheCallersOwnTierRatherThanAssertingAdmin()
    {
        // The review actions are admin-gated upstream, so in practice this IS admin. It is read from the
        // session rather than hard-coded, because a literal is only correct for as long as every caller of
        // the method stays behind that gate — and nothing about the method would show it if one didn't.
        using AssistantClient client = Client();

        using HttpResponseMessage? response = await client.GetReviewUsersAsync(Caller(KgsmTier.Operator), default);

        Assert.Equal("operator", _received!["X-Relay-Tier"]);
    }

    [Fact]
    public async Task ATurnForwardsTheTierAndTheSeparateAutoRunDecision()
    {
        // Auto-run stays its own header: it is admin tier AND a per-turn user toggle, so it is a preference
        // riding a permission and cannot be re-derived from the tier alone.
        using AssistantClient client = Client();

        using HttpResponseMessage? response = await client.OpenTurnStreamAsync(
            new { prompt = "hi" }, Caller(KgsmTier.Admin), autoAct: true, conversationId: null, default);

        Assert.Equal("admin", _received!["X-Relay-Tier"]);
        Assert.Equal("true", _received["X-Relay-Auto-Act"]);
    }

    [Fact]
    public async Task AnAdminWhoDidNotAskForAutoRunDoesNotGetIt()
    {
        using AssistantClient client = Client();

        using HttpResponseMessage? response = await client.OpenTurnStreamAsync(
            new { prompt = "hi" }, Caller(KgsmTier.Admin), autoAct: false, conversationId: null, default);

        Assert.Equal("false", _received!["X-Relay-Auto-Act"]);
    }

    [Fact]
    public async Task AControlCharacterInADisplayNameCannotSplitAHeader()
    {
        // The display name is user-controlled Discord text crossing a trust boundary. A CR/LF in it would
        // otherwise let the caller append headers of their own choosing — including a higher tier.
        using AssistantClient client = Client();
        var caller = new RelayPrincipal("198772043", "Haru\r\nX-Relay-Tier: admin", KgsmTier.Viewer);

        using HttpResponseMessage? response = await client.GetConversationsAsync(caller, default);

        Assert.Equal("viewer", _received!["X-Relay-Tier"]);
        Assert.DoesNotContain("\r", _received["X-Relay-User-Name"]!);
        Assert.DoesNotContain("\n", _received["X-Relay-User-Name"]!);
    }

    [Fact]
    public void TheWireSpellingsAreTheOnesTheAssistantParses()
    {
        // The two sides agree only because both go through KgsmTiers. If this ever drifted, every relayed
        // caller would silently floor to None on the far side — a total loss of authority, not a typo.
        Assert.Equal("viewer", KgsmTiers.ToWire(KgsmTier.Viewer));
        Assert.Equal("operator", KgsmTiers.ToWire(KgsmTier.Operator));
        Assert.Equal("admin", KgsmTiers.ToWire(KgsmTier.Admin));

        Assert.Equal(KgsmTier.Operator, KgsmTiers.Parse(KgsmTiers.ToWire(KgsmTier.Operator)));
        Assert.Equal(KgsmTier.None, KgsmTiers.Parse("something-else"));
        Assert.Equal(KgsmTier.None, KgsmTiers.Parse(""));
        Assert.Equal(KgsmTier.None, KgsmTiers.Parse(null));
    }
}
