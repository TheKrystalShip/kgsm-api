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

    // The shape kgsm-bot's build emits, trimmed to two commands — one that reads, one that acts.
    private const string BotManifest = """
        {
          "schemaVersion": 1,
          "leaf": "bot",
          "surface": "discord",
          "gate": "none",
          "commands": [
            { "name": "list", "description": "List all game server instances", "mutates": false, "options": [] },
            {
              "name": "start", "description": "Start up a game server", "mutates": true,
              "options": [
                { "name": "instance", "description": "Game server instance",
                  "type": "string", "required": true, "autocomplete": true }
              ]
            }
          ]
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

    [Fact]
    public async Task TheManifestReachesTheWireAsTheLeafWroteIt()
    {
        using var factory = new LeafTestFactory();
        factory.InstallCommands("bot", BotManifest);

        JsonElement body = await Get(Client(factory, KgsmTier.Operator), "bot");

        Assert.Equal("bot", body.GetProperty("leaf").GetString());
        Assert.Equal("discord", body.GetProperty("surface").GetString());
        // The leaf's own statement about what it checks before acting. The API cannot verify a gate it does
        // not implement, so it must neither soften nor restate this.
        Assert.Equal("none", body.GetProperty("gate").GetString());

        JsonElement[] commands = body.GetProperty("commands").EnumerateArray().ToArray();
        Assert.Equal(["list", "start"], commands.Select(c => c.GetProperty("name").GetString()));

        JsonElement start = commands.Single(c => c.GetProperty("name").GetString() == "start");
        Assert.True(start.GetProperty("mutates").GetBoolean());
        Assert.Equal("Start up a game server", start.GetProperty("description").GetString());

        JsonElement option = start.GetProperty("options").EnumerateArray().Single();
        Assert.Equal("instance", option.GetProperty("name").GetString());
        Assert.Equal("string", option.GetProperty("type").GetString());
        Assert.True(option.GetProperty("required").GetBoolean());
        Assert.True(option.GetProperty("autocomplete").GetBoolean());

        Assert.False(commands.Single(c => c.GetProperty("name").GetString() == "list").GetProperty("mutates").GetBoolean());
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
            { "schemaVersion": 1, "leaf": "bot", "surface": "discord", "gate": "none",
              "commands": [ { "name": "ping", "description": "Check if the bot is responsive", "mutates": false } ] }
            """);

        JsonElement body = await Get(Client(factory, KgsmTier.Operator), "bot");

        Assert.Empty(body.GetProperty("commands").EnumerateArray().Single().GetProperty("options").EnumerateArray());
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
    [InlineData("""{ "schemaVersion": 2, "leaf": "bot", "surface": "discord", "gate": "none", "commands": [] }""")]
    [InlineData("""{ "schemaVersion": 1, "leaf": "assistant", "surface": "discord", "gate": "none", "commands": [] }""")]
    [InlineData("""{ "schemaVersion": 1, "leaf": "bot", "surface": "discord", "gate": "none", "commands": [ { "name": "" } ] }""")]
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

        Assert.Equal(2, body.GetProperty("commands").GetArrayLength());
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
