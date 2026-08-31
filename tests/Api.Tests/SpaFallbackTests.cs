using System.Net;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// What the SPA fallback answers for, and the two things it must never answer for.
/// </summary>
/// <remarks>
/// The shell is a <c>200</c>, so a caller cannot tell "this route is gone" from "here is a web page"
/// by status alone and will conclude the route still exists. That is not hypothetical: two separate
/// readings of a deleted endpoint reported it as still present, both from a <c>200</c> that was the
/// shell.
/// </remarks>
public sealed class SpaFallbackTests : IDisposable
{
    private readonly string _webRoot;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public SpaFallbackTests()
    {
        // A real wwwroot with a real index.html, because the fallback is only mapped when one exists —
        // a test host without it would exercise nothing and pass.
        _webRoot = Path.Combine(Path.GetTempPath(), "kgsm-api-spa-fallback", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_webRoot);
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), "<!doctype html><title>panel</title>");

        _factory = new AuthTestFactory().WithWebHostBuilder(b => b.UseWebRoot(_webRoot));
        _client = _factory.CreateClient();
    }

    [Fact]
    public async Task AClientRoutedDeepLinkBootsTheApp()
    {
        HttpResponseMessage response = await _client.GetAsync("/servers/some-instance");

        // No session, and it still loads: the bundle is a public static site and the data under it
        // is what is gated.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("/api/v1/no-such-thing")]
    [InlineData("/auth/session/no-such-thing")]
    public async Task ARouteThatDoesNotExistIsNotAWebPage(string path)
    {
        // /auth sits at the root beside /api rather than under it, so naming only /api leaves the
        // whole auth surface answering a web page for paths that do not exist.
        HttpResponseMessage response = await _client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotEqual("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("/auth/session/cluster-exchange")]
    [InlineData("/somewhere-a-person-might-navigate")]
    public async Task AWriteToARouteThatDoesNotExistIsNotAWebPage(string path)
    {
        // Nothing client-routed arrives as a POST — a deep link is a navigation. So a POST that
        // matched no controller is a caller in error and is owed an answer that says so.
        HttpResponseMessage response = await _client.PostAsync(path, new StringContent(""));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_shell_that_has_gone_since_startup_is_missing_rather_than_broken()
    {
        // The bundle is a directory a deploy replaces and an operator can empty. Deciding once at
        // startup that a panel is here leaves the fallback holding a path that can stop existing, and
        // sending a file that is not there throws — an unhandled 500 and a stack trace per request,
        // for a host whose only fault is no longer having a panel.
        File.Delete(Path.Combine(_webRoot, "index.html"));

        HttpResponseMessage response = await _client.GetAsync("/servers/some-instance");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        try { Directory.Delete(_webRoot, recursive: true); } catch (IOException) { }
    }
}
