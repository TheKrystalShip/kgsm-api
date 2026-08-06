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
/// forwarded headers are what restore the client's real scheme and address — and the scheme is not
/// cosmetic here, because <c>AuthController</c> writes the OAuth CSRF state cookie
/// <c>Secure = Request.IsHttps</c>. Get this wrong and a browser login keeps working while quietly
/// dropping to a non-Secure cookie, which is precisely the kind of failure nothing notices.
/// </para>
/// <para>
/// The header is only ever honoured from a trusted peer, so these tests set the connection's remote
/// address explicitly rather than relying on whatever the test host leaves there.
/// </para>
/// </remarks>
public sealed class ForwardedHeadersTests
{
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
    /// Starts a login and reports whether the handshake cookie came back marked Secure — the
    /// observable consequence of the app believing the request was https.
    /// </summary>
    private static async Task<bool> StateCookieIsSecureAsync(
        WebApplicationFactory<Program> factory, string? forwardedProto)
    {
        HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var request = new HttpRequestMessage(HttpMethod.Get, "/auth/discord/start");
        if (forwardedProto is not null)
            request.Headers.Add("X-Forwarded-Proto", forwardedProto);

        using HttpResponseMessage response = await client.SendAsync(request);

        string cookie = response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values)
            ? values.First(c => c.StartsWith("kgsm_oauth_state=", StringComparison.Ordinal))
            : throw new InvalidOperationException("the login did not set a handshake cookie");

        return cookie.Contains("secure", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AProxyOnThisMachineIsBelievedAboutTheScheme()
    {
        using WebApplicationFactory<Program> factory = Factory(IPAddress.Loopback);

        Assert.True(await StateCookieIsSecureAsync(factory, "https"));
    }

    [Fact]
    public async Task AnIPv6LoopbackProxyIsBelievedToo()
    {
        // The proxy may reach us over either loopback family depending on how it resolves the
        // upstream; trusting only one would make the cookie's Secure flag depend on that detail.
        using WebApplicationFactory<Program> factory = Factory(IPAddress.IPv6Loopback);

        Assert.True(await StateCookieIsSecureAsync(factory, "https"));
    }

    [Fact]
    public async Task AForgedHeaderFromTheInternetIsIgnored()
    {
        // The whole trust model. Anyone can send X-Forwarded-Proto; only a peer we recognise as our
        // own proxy is believed. This is also what makes the middleware safe to run with no proxy in
        // front at all.
        using WebApplicationFactory<Program> factory = Factory(IPAddress.Parse("203.0.113.7"));

        Assert.False(await StateCookieIsSecureAsync(factory, "https"));
    }

    [Fact]
    public async Task WithNoForwardedHeaderThePlainRequestIsTakenAtFaceValue()
    {
        // A direct plain-http caller — a loopback ops call today. Nothing claims https, so nothing
        // pretends it was.
        using WebApplicationFactory<Program> factory = Factory(IPAddress.Loopback);

        Assert.False(await StateCookieIsSecureAsync(factory, forwardedProto: null));
    }

    [Fact]
    public async Task AProxyThatReportsPlainHttpIsBelievedToo()
    {
        // Trust runs both directions: a proxy saying the client spoke http must not be upgraded into
        // https just because there is a proxy in the path.
        using WebApplicationFactory<Program> factory = Factory(IPAddress.Loopback);

        Assert.False(await StateCookieIsSecureAsync(factory, "http"));
    }

    [Fact]
    public void TheTrustedSetIsExactlyAProxyOnThisMachine()
    {
        // The scheme tests above prove the trust decision through its most consequential effect; this
        // pins the configuration itself, where the blast radius of a mistake is widest. A single
        // stray entry — a bare network, a default left in place — would let a stranger assert their
        // own scheme and address.
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
