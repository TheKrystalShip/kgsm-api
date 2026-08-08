using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using TheKrystalShip.Api.Services.Auth;

using TheKrystalShip.KGSM.Auth;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// <c>GET /hosts/{id}/services/{leaf}/commands</c> — the catalog a leaf ships of the commands it answers to.
/// The API scans a directory and passes the leaf's own words through: it holds no list of leaves and no idea
/// what any command does, so what these tests pin is that a manifest arrives intact, that a leaf without one
/// is a 404 rather than an empty list, and that a file it cannot trust is skipped instead of half-read.
/// </summary>
public sealed class LeafCommandsApiTests
{
    private const string Host = AuthTestFactory.HostId;

    // The shape kgsm-bot's build emits, trimmed to two commands — one that reads, one that acts, each
    // under the gate that admits it.
    private const string BotManifest = """
        {
          "schemaVersion": 2,
          "leaf": "bot",
          "surface": "discord",
          "gates": {
            "none": [
              { "name": "list", "description": "List all game server instances", "mutates": false, "options": [] }
            ],
            "operator": [
              {
                "name": "start", "description": "Start up a game server", "mutates": true,
                "options": [
                  { "name": "instance", "description": "Game server instance",
                    "type": "string", "required": true, "autocomplete": true }
                ]
              }
            ]
          }
        }
        """;

    private static HttpClient Client(LeafTestFactory f, KgsmTier tier)
    {
        HttpClient c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", f.AccessToken(tier));
        return c;
    }

    private static async Task<JsonElement> Get(HttpClient c, string leaf)
    {
        HttpResponseMessage resp = await c.GetAsync($"/api/v1/hosts/{Host}/services/{leaf}/commands");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
    }

    // The shape kgsm-llm's build emits: the catalog keyed by the gate that admits each command.
    private const string AssistantManifest = """
        {
          "schemaVersion": 2,
          "leaf": "assistant",
          "surface": "chat",
          "gates": {
            "viewer": [
              { "name": "compact", "description": "Summarize this conversation", "mutates": true, "options": [] }
            ],
            "admin": [
              {
                "name": "autorun", "description": "Whether actions run without confirmation", "mutates": true,
                "options": [
                  { "name": "state", "description": "Whether auto-run is on.",
                    "type": "string", "required": false, "autocomplete": true, "values": ["on", "off"] }
                ]
              }
            ]
          }
        }
        """;

    /// <summary>All the commands in a manifest, whichever gate each sits under.</summary>
    private static JsonElement[] AllCommands(JsonElement body) =>
        [.. body.GetProperty("gates").EnumerateObject().SelectMany(g => g.Value.EnumerateArray())];

    [Fact]
    public async Task TheManifestReachesTheWireAsTheLeafWroteIt()
    {
        using var factory = new LeafTestFactory();
        factory.InstallCommands("assistant", AssistantManifest);

        JsonElement body = await Get(Client(factory, KgsmTier.Operator), "assistant");

        Assert.Equal("assistant", body.GetProperty("leaf").GetString());
        Assert.Equal("chat", body.GetProperty("surface").GetString());

        // The leaf's own statement about what it checks before running each command. The API cannot verify
        // a gate it does not implement, so it must neither soften nor restate this.
        JsonElement gates = body.GetProperty("gates");
        Assert.Equal(["compact"], gates.GetProperty("viewer").EnumerateArray().Select(c => c.GetProperty("name").GetString()));
        Assert.Equal(["autorun"], gates.GetProperty("admin").EnumerateArray().Select(c => c.GetProperty("name").GetString()));

        JsonElement option = gates.GetProperty("admin").EnumerateArray().Single()
            .GetProperty("options").EnumerateArray().Single();
        Assert.Equal("state", option.GetProperty("name").GetString());
        Assert.False(option.GetProperty("required").GetBoolean());
        Assert.True(option.GetProperty("autocomplete").GetBoolean());
        // The fixed set the option offers, which is what lets a surface complete it without asking the leaf.
        Assert.Equal(["on", "off"], option.GetProperty("values").EnumerateArray().Select(v => v.GetString()));
    }

    /// <summary>
    /// The other surface, whose options are free text rather than a fixed set: a Discord option's
    /// suggestions come from the bot as someone types, so the file offers no <c>values</c> and a client
    /// has to read that as free text rather than as an empty set of choices.
    /// </summary>
    [Fact]
    public async Task AFreeTextOptionOffersNoValues()
    {
        using var factory = new LeafTestFactory();
        factory.InstallCommands("bot", BotManifest);

        JsonElement body = await Get(Client(factory, KgsmTier.Operator), "bot");
        JsonElement gates = body.GetProperty("gates");

        Assert.Equal(["start"], gates.GetProperty("operator").EnumerateArray().Select(c => c.GetProperty("name").GetString()));
        Assert.Equal(["list"], gates.GetProperty("none").EnumerateArray().Select(c => c.GetProperty("name").GetString()));

        JsonElement start = gates.GetProperty("operator").EnumerateArray().Single();
        Assert.True(start.GetProperty("mutates").GetBoolean());
        Assert.Equal("Start up a game server", start.GetProperty("description").GetString());

        JsonElement option = start.GetProperty("options").EnumerateArray().Single();
        Assert.Equal("instance", option.GetProperty("name").GetString());
        Assert.Equal("string", option.GetProperty("type").GetString());
        Assert.True(option.GetProperty("required").GetBoolean());
        Assert.True(option.GetProperty("autocomplete").GetBoolean());
        Assert.Equal(JsonValueKind.Null, option.GetProperty("values").ValueKind);
    }

    /// <summary>
    /// A command declaring no options at all is "takes no options", not an absent list — the panel prints
    /// what to type, and a null there is a hole in that answer.
    /// </summary>
    [Fact]
    public async Task ACommandWithNoOptionsArrivesWithAnEmptyList()
    {
        using var factory = new LeafTestFactory();
        factory.InstallCommands("bot", """
            { "schemaVersion": 2, "leaf": "bot", "surface": "discord",
              "gates": { "none": [ { "name": "ping", "description": "Check if the bot is responsive", "mutates": false } ] } }
            """);

        JsonElement body = await Get(Client(factory, KgsmTier.Operator), "bot");

        Assert.Empty(AllCommands(body).Single().GetProperty("options").EnumerateArray());
    }

    /// <summary>
    /// Most leaves take no commands, and saying so is a 404. An empty list would read as "this one takes
    /// commands and has none right now", which is a different and untrue statement.
    /// </summary>
    [Fact]
    public async Task ALeafThatShipsNoManifestIs404()
    {
        using var factory = new LeafTestFactory();
        factory.InstallCommands("bot", BotManifest);
        HttpClient client = Client(factory, KgsmTier.Operator);

        HttpResponseMessage resp = await client.GetAsync($"/api/v1/hosts/{Host}/services/monitor/commands");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        JsonElement error = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.True(error.TryGetProperty("error", out _), "404s carry the frozen error envelope");
    }

    [Fact]
    public async Task AForeignHostIdIs404()
    {
        using var factory = new LeafTestFactory();
        factory.InstallCommands("bot", BotManifest);

        HttpResponseMessage resp = await Client(factory, KgsmTier.Operator)
            .GetAsync("/api/v1/hosts/some-other-box/services/bot/commands");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    /// <summary>
    /// A file this API cannot trust is skipped whole. Every case here would otherwise reach an operator as
    /// instructions to type something: a manifest written to a newer schema, one installed under a name that
    /// is not the leaf it describes, one with a nameless command, and one that is not JSON at all.
    /// </summary>
    [Theory]
    // A version this build does not know: the rest of the file may mean something else entirely. The
    // retired flat-list shape is exactly that — a file still written that way is skipped whole rather
    // than half-read as though its `gate` meant what this build's `gates` means.
    [InlineData("""{ "schemaVersion": 99, "leaf": "bot", "surface": "discord", "gates": {} }""")]
    [InlineData("""{ "schemaVersion": 1, "leaf": "bot", "surface": "discord", "gate": "none", "commands": [] }""")]
    // Installed under a name that is not the leaf it describes.
    [InlineData("""{ "schemaVersion": 2, "leaf": "assistant", "surface": "chat", "gates": {} }""")]
    // A nameless command — the panel would print it as something to type.
    [InlineData("""{ "schemaVersion": 2, "leaf": "bot", "surface": "discord", "gates": { "none": [ { "name": "" } ] } }""")]
    // No catalog, and no surface to print.
    [InlineData("""{ "schemaVersion": 2, "leaf": "bot", "surface": "discord" }""")]
    [InlineData("""{ "schemaVersion": 2, "leaf": "bot", "gates": { "none": [] } }""")]
    [InlineData("this is not json")]
    public async Task AManifestThatCannotBeTrustedIsSkipped(string json)
    {
        using var factory = new LeafTestFactory();
        factory.InstallCommands("bot", json);

        HttpResponseMessage resp = await Client(factory, KgsmTier.Operator)
            .GetAsync($"/api/v1/hosts/{Host}/services/bot/commands");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    /// <summary>One leaf's bad file must not take another leaf's list with it.</summary>
    [Fact]
    public async Task ABadManifestDoesNotHideAGoodOne()
    {
        using var factory = new LeafTestFactory();
        factory.InstallCommands("bot", BotManifest);
        factory.InstallCommands("assistant", "{ oh dear");

        JsonElement body = await Get(Client(factory, KgsmTier.Operator), "bot");

        Assert.Equal(2, AllCommands(body).Length);
    }

    /// <summary>
    /// Read-only reference material about a leaf, gated with the rest of the Services reads: operator sees
    /// it, viewer does not, no bearer at all is a 401 rather than a 403.
    /// </summary>
    [Fact]
    public async Task ItIsGatedAtOperator()
    {
        using var factory = new LeafTestFactory();
        factory.InstallCommands("bot", BotManifest);
        string path = $"/api/v1/hosts/{Host}/services/bot/commands";

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(factory, KgsmTier.Viewer).GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client(factory, KgsmTier.Operator).GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client(factory, KgsmTier.Admin).GetAsync(path)).StatusCode);
    }
}
