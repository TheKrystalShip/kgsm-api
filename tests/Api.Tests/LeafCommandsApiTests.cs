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
    // naming the action that admits it.
    private const string BotManifest = """
        {
          "schemaVersion": 3,
          "leaf": "bot",
          "surface": "discord",
          "commands": [
            { "name": "list", "description": "List all game server instances", "action": "kgsm:server.read",
              "mutates": false, "options": [] },
            {
              "name": "start", "description": "Start up a game server", "action": "kgsm:server.start",
              "mutates": true,
              "options": [
                { "name": "instance", "description": "Game server instance",
                  "type": "string", "required": true, "autocomplete": true }
              ]
            }
          ]
        }
        """;

    private static HttpClient Client(LeafTestFactory f, Persona persona)
    {
        HttpClient c = f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", f.AccessToken(persona));
        return c;
    }

    private static async Task<JsonElement> Get(HttpClient c, string leaf)
    {
        HttpResponseMessage resp = await c.GetAsync($"/api/v1/hosts/{Host}/services/{leaf}/commands");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
    }

    // The shape kgsm-llm's build emits: every command, each naming the action that admits it.
    private const string AssistantManifest = """
        {
          "schemaVersion": 3,
          "leaf": "assistant",
          "surface": "chat",
          "commands": [
            { "name": "compact", "description": "Summarize this conversation", "action": "assistant:chat",
              "mutates": true, "options": [] },
            {
              "name": "autorun", "description": "Whether actions run without confirmation",
              "action": "assistant:autorun", "mutates": true,
              "options": [
                { "name": "state", "description": "Whether auto-run is on.",
                  "type": "string", "required": false, "autocomplete": true, "values": ["on", "off"] }
              ]
            }
          ]
        }
        """;

    private static JsonElement[] AllCommands(JsonElement body) => [.. body.GetProperty("commands").EnumerateArray()];

    [Fact]
    public async Task TheManifestReachesTheWireAsTheLeafWroteIt()
    {
        using var factory = new LeafTestFactory();
        factory.InstallCommands("assistant", AssistantManifest);

        JsonElement body = await Get(Client(factory, Persona.Runner), "assistant");

        Assert.Equal("assistant", body.GetProperty("leaf").GetString());
        Assert.Equal("chat", body.GetProperty("surface").GetString());

        // The leaf's own statement about what it checks before running each command. The API cannot verify
        // a check it does not implement, so it must neither soften nor restate this.
        Assert.False(body.TryGetProperty("gates", out _));
        JsonElement[] commands = [.. body.GetProperty("commands").EnumerateArray()];
        Assert.Equal(
            [("compact", "assistant:chat"), ("autorun", "assistant:autorun")],
            commands.Select(c => (c.GetProperty("name").GetString(), c.GetProperty("action").GetString())));

        JsonElement option = commands.Single(c => c.GetProperty("name").GetString() == "autorun")
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

        JsonElement body = await Get(Client(factory, Persona.Runner), "bot");

        Assert.Equal(
            [("list", "kgsm:server.read"), ("start", "kgsm:server.start")],
            AllCommands(body).Select(c => (c.GetProperty("name").GetString(), c.GetProperty("action").GetString())));

        JsonElement start = AllCommands(body).Single(c => c.GetProperty("name").GetString() == "start");
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
            { "schemaVersion": 3, "leaf": "bot", "surface": "discord",
              "commands": [ { "name": "ping", "description": "Check if the bot is responsive",
                              "action": "bot:status.read", "mutates": false } ] }
            """);

        JsonElement body = await Get(Client(factory, Persona.Runner), "bot");

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
        HttpClient client = Client(factory, Persona.Runner);

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

        HttpResponseMessage resp = await Client(factory, Persona.Runner)
            .GetAsync("/api/v1/hosts/some-other-box/services/bot/commands");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    /// <summary>
    /// A file this API cannot trust is skipped whole. Every case here would otherwise reach an operator as
    /// instructions to type something: a manifest written to another schema, one installed under a name
    /// that is not the leaf it describes, a nameless command, a command naming no action, and one that is
    /// not JSON at all.
    /// </summary>
    [Theory]
    // A version this build does not know: the rest of the file may mean something else entirely, so a
    // file still keyed by buckets is skipped whole rather than half-read.
    [InlineData("""{ "schemaVersion": 99, "leaf": "bot", "surface": "discord", "commands": [] }""")]
    [InlineData("""{ "schemaVersion": 2, "leaf": "bot", "surface": "discord", "gates": { "none": [] } }""")]
    // Installed under a name that is not the leaf it describes.
    [InlineData("""{ "schemaVersion": 3, "leaf": "assistant", "surface": "chat", "commands": [] }""")]
    // A nameless command — the panel would print it as something to type.
    [InlineData("""{ "schemaVersion": 3, "leaf": "bot", "surface": "discord", "commands": [ { "name": "", "action": "bot:status.read" } ] }""")]
    // A command naming no action — the panel would print it as open to anybody.
    [InlineData("""{ "schemaVersion": 3, "leaf": "bot", "surface": "discord", "commands": [ { "name": "ping", "description": "d", "mutates": false } ] }""")]
    // No catalog, and no surface to print.
    [InlineData("""{ "schemaVersion": 3, "leaf": "bot", "surface": "discord" }""")]
    [InlineData("""{ "schemaVersion": 3, "leaf": "bot", "commands": [] }""")]
    [InlineData("this is not json")]
    public async Task AManifestThatCannotBeTrustedIsSkipped(string json)
    {
        using var factory = new LeafTestFactory();
        factory.InstallCommands("bot", json);

        HttpResponseMessage resp = await Client(factory, Persona.Runner)
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

        JsonElement body = await Get(Client(factory, Persona.Runner), "bot");

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
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(factory, Persona.Reader).GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client(factory, Persona.Runner).GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client(factory, Persona.Owner).GetAsync(path)).StatusCode);
    }
}
