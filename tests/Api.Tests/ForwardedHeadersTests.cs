using System.Net;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// What the app believes about a request that reached it through a reverse proxy.
/// </summary>
/// <remarks>
/// <para>
/// Behind a proxy the request this app sees is the <em>proxy's</em>: plain http, from loopback. The
/// forwarded headers are what restore the client's real scheme and address — and both matter at once
/// here, because the plain-HTTP upgrade gate decides on them together: a plain-http caller out on the
/// internet is redirected to https, and anything else is served. Believe the wrong scheme and a browser
/// the proxy already served over TLS is bounced back to https forever; believe the wrong address and a
/// stranger is treated as the operator's own network.
/// </para>
/// <para>
/// The headers are only ever honoured from a trusted peer, so these tests set the connection's remote
/// address explicitly rather than relying on whatever the test host leaves there, and read the verdict
/// off that gate.
/// </para>
/// </remarks>
public sealed class ForwardedHeadersTests
{
    /// <summary>A client out on the internet, as the proxy reports it.</summary>
    private const string InternetClient = "203.0.113.7";

    /// <summary>
    /// Stamps the connection's remote address before the app's own pipeline runs, standing in for
    /// "who was the immediate peer" — the fact the forwarded-headers trust decision turns on, and the
    /// one thing an in-memory test server does not supply on its own.
    /// </summary>
    private sealed class RemoteAddressFilter(IPAddress? address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = address;
                await nextMiddleware();
            });
            next(app);
        };
    }

    private static WebApplicationFactory<Program> Factory(IPAddress? peer) =>
        new AuthTestFactory().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.AddSingleton<IStartupFilter>(new RemoteAddressFilter(peer))));

    /// <summary>
    /// Asks an open endpoint and reports whether the upgrade gate sent the caller to https — the
    /// observable consequence of the app believing it was a plain-http caller on the internet.
    /// </summary>
    private static async Task<bool> SentToHttpsAsync(
        WebApplicationFactory<Program> factory, string? forwardedFor, string? forwardedProto)
    {
        HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        if (forwardedFor is not null)
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        if (forwardedProto is not null)
            request.Headers.Add("X-Forwarded-Proto", forwardedProto);

        using HttpResponseMessage response = await client.SendAsync(request);
        return response.StatusCode == HttpStatusCode.PermanentRedirect;
    }

    [Fact]
    public async Task AProxyOnThisMachineIsBelievedAboutTheScheme()
    {
        // An internet client the proxy served over TLS. Believed, so it is not sent round again.
        using WebApplicationFactory<Program> factory = Factory(IPAddress.Loopback);

        Assert.False(await SentToHttpsAsync(factory, InternetClient, "https"));
    }

    [Fact]
    public async Task AnIPv6LoopbackProxyIsBelievedToo()
    {
        // The proxy may reach us over either loopback family depending on how it resolves the
        // upstream; trusting only one would make the verdict depend on that detail.
        using WebApplicationFactory<Program> factory = Factory(IPAddress.IPv6Loopback);

        Assert.False(await SentToHttpsAsync(factory, InternetClient, "https"));
    }

    [Fact]
    public async Task AProxyThatReportsPlainHttpIsBelievedToo()
    {
        // Trust runs both directions: a proxy saying an internet client spoke http must not be upgraded
        // into https just because there is a proxy in the path — so that client is sent to https.
        using WebApplicationFactory<Program> factory = Factory(IPAddress.Loopback);

        Assert.True(await SentToHttpsAsync(factory, InternetClient, "http"));
    }

    [Fact]
    public async Task AForgedHeaderFromTheInternetIsIgnored()
    {
        // The whole trust model. Anyone can send X-Forwarded-Proto; only a peer we recognise as our
        // own proxy is believed. This is also what makes the middleware safe to run with no proxy in
        // front at all. Had the forged header been believed the request would have counted as https
        // and sailed past the gate, so the redirect IS the observation that it was not.
        using WebApplicationFactory<Program> factory = Factory(IPAddress.Parse(InternetClient));

        Assert.True(await SentToHttpsAsync(factory, forwardedFor: null, "https"));
    }

    [Fact]
    public async Task WithNoForwardedHeaderThePlainRequestIsTakenAtFaceValue()
    {
        // A direct plain-http caller on loopback — the deploy's own health check. Nothing claims
        // otherwise, so nothing pretends it was anything else, and loopback crosses no network.
        using WebApplicationFactory<Program> factory = Factory(IPAddress.Loopback);

        Assert.False(await SentToHttpsAsync(factory, forwardedFor: null, forwardedProto: null));
    }

    [Fact]
    public void TheTrustedSetIsExactlyAProxyOnThisMachine()
    {
        // The tests above prove the trust decision through its effect; this pins the configuration
        // itself, where the blast radius of a mistake is widest. A single stray entry — a bare network,
        // a default left in place — would let a stranger assert their own scheme and address.
        using WebApplicationFactory<Program> factory = Factory(IPAddress.Loopback);

        var options = factory.Services
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<
                Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>>().Value;

        Assert.Equal(
            [IPAddress.Loopback, IPAddress.IPv6Loopback],
            options.KnownProxies);
        Assert.Empty(options.KnownIPNetworks);
        // One hop. A longer chain would mean believing a header some earlier party appended.
        Assert.Equal(1, options.ForwardLimit);
        // X-Forwarded-Host is deliberately absent: the proxy passes the original Host through, so
        // there is nothing to reconstruct and one fewer header to believe.
        Assert.Equal(
            Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto,
            options.ForwardedHeaders);
    }
}
