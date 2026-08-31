using System.Net;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// What a host with no panel answers to a path that is not one of its routes.
/// </summary>
/// <remarks>
/// <para>
/// A node does not have to serve the Control Panel. The panel is a static artifact that can be hosted
/// anywhere, and a cluster whose panel is served from somewhere else leaves every node with an empty
/// web root — which is an ordinary node, and has to read as one.
/// </para>
/// <para>
/// The failure this guards is that it did not. With no fallback mapped, an unmatched path meets the
/// global <c>RequireAuthenticatedUser</c> policy — which applies to a request with no endpoint at all
/// — and answers <c>401</c>: "sign in and you will see it", about a path that does not exist. On a
/// node whose cluster has an anchor that is doubly wrong, because signing in there is exactly what it
/// refuses to let anybody do.
/// </para>
/// </remarks>
public sealed class NoPanelFallbackTests : IDisposable
{
    private readonly string _webRoot;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public NoPanelFallbackTests()
    {
        // A web root that exists and is empty — what a node looks like once its bundle is gone. An
        // absent directory would be a different case and would not exercise this one.
        _webRoot = Path.Combine(Path.GetTempPath(), "kgsm-api-no-panel", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_webRoot);

        _factory = new AuthTestFactory().WithWebHostBuilder(b => b.UseWebRoot(_webRoot));
        _client = _factory.CreateClient();
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/servers/some-instance")]
    [InlineData("/index.html")]
    [InlineData("/anything-at-all")]
    public async Task An_unmatched_path_is_not_found_rather_than_unauthorized(string path)
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync(path)).StatusCode);
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/api/v1")]
    public async Task The_routes_it_does_serve_are_untouched(string path)
    {
        // Losing a panel is not losing a node. Whatever this host answers for on its own account it
        // goes on answering for, and a liveness probe is the one a fleet reads.
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync(path)).StatusCode);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        try { Directory.Delete(_webRoot, recursive: true); } catch (IOException) { }
    }
}
