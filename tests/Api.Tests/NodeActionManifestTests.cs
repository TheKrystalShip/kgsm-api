using System.Net;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Auth.Access;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// <c>GET /.well-known/tks-actions.json</c>: the actions this node's engine, leaves and anchors perform,
/// for the provider to pull. Public, one manifest per component, in the application format.
/// </summary>
public sealed class NodeActionManifestTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kgsm-api-actions-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void Install(string directory, string file, string component, string actions)
    {
        string path = Path.Combine(_root, directory, "actions");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, file),
            $$"""{"schemaVersion":1,"component":"{{component}}","version":"1.0.0","actions":[{{actions}}]}""");
    }

    private HttpClient Client()
    {
        Install("leaves", "api.json", "api",
            """{"id":"servers.read","title":"View servers","effect":"read","scope":"node"},{"id":"alerts.read","title":"View alerts","effect":"read","scope":"instance","self":false}""");
        Install("leaves", "kgsm.json", "kgsm", """{"id":"server.stop","title":"Stop a server","effect":"execute","scope":"instance"}""");
        Install("anchors", "dns-anchor.json", "dns", """{"id":"aliases.write","title":"Edit names","effect":"write","scope":"cluster"}""");
        Install("anchors", "auth-anchor.json", "auth", """{"id":"users.read","title":"Read accounts","effect":"read","scope":"cluster"}""");

        return new AuthTestFactory().WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Api:LeafDescriptorDir"] = Path.Combine(_root, "leaves"),
                ["Api:AnchorDescriptorDir"] = Path.Combine(_root, "anchors"),
            }))).CreateClient();
    }

    [Fact]
    public async Task Served_anonymously_as_one_manifest_per_component_without_the_providers_own()
    {
        using HttpClient client = Client();

        using HttpResponseMessage response = await client.GetAsync(NodeActionManifest.Path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        NodeActionManifest document = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(), NodeActionManifestJson.Default.NodeActionManifest)!;

        Assert.Equal(1, document.SchemaVersion);
        Assert.Equal(["api", "dns", "kgsm"], document.Manifests.Select(m => m.Component));
        Assert.Equal(["alerts.read", "servers.read"], document.Manifests[0].Actions!.Select(a => a.Id));
    }

    [Fact]
    public async Task Every_action_is_at_the_one_scope_an_application_has_and_declares_no_requirements()
    {
        using HttpClient client = Client();

        string body = await client.GetStringAsync(NodeActionManifest.Path);
        NodeActionManifest document = JsonSerializer.Deserialize(
            body, NodeActionManifestJson.Default.NodeActionManifest)!;

        Assert.All(document.Manifests.SelectMany(m => m.Actions!), a => Assert.Equal("cluster", a.Scope));
        Assert.All(document.Manifests, m => Assert.Empty(m.Requires!));
        Assert.DoesNotContain("\"self\":false", body);
    }
}
