using System.Net;
using System.Net.Http.Headers;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using TheKrystalShip.KGSM.Sessions.Testing;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// What this node accepts as a person: the access token the sign-in provider issued, checked against the
/// keys the provider publishes, for this cluster's audience, and nothing else.
/// </summary>
public sealed class SessionAcceptanceTests(AuthTestFactory factory) : IClassFixture<AuthTestFactory>
{
    private static HttpRequestMessage Get(string path, string token)
    {
        HttpRequestMessage request = new(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    [Fact]
    public async Task A_token_the_provider_issued_is_accepted_and_its_actions_are_what_the_person_may_do()
    {
        using HttpClient client = factory.CreateClient();
        string token = factory.AccessToken(Persona.Reader);

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Get("/api/v1/servers", token))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Get("/api/v1/integrations", token))).StatusCode);
    }

    [Fact]
    public async Task A_refusal_says_which_action_the_request_needs()
    {
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.SendAsync(
            Get("/api/v1/integrations", factory.AccessToken(Persona.None)));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"code\":\"forbidden\"", body);
        Assert.Contains("This needs ", body);
    }

    [Fact]
    public async Task A_token_signed_by_a_key_the_provider_never_published_is_refused()
    {
        using HttpClient client = factory.CreateClient();
        string forged = AuthTestFactory.Provider.Mint(
            TestIdentity.Person.Subject, actions: Personas.AllActions, signedBy: new TestIssuer());

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Get("/api/v1/servers", forged))).StatusCode);
    }

    [Fact]
    public async Task A_token_from_another_issuer_is_refused()
    {
        using HttpClient client = factory.CreateClient();
        string token = AuthTestFactory.Provider.Mint(
            TestIdentity.Person.Subject, actions: Personas.AllActions, issuer: "https://elsewhere.test");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Get("/api/v1/servers", token))).StatusCode);
    }

    [Fact]
    public async Task A_token_for_another_application_is_refused()
    {
        using HttpClient client = factory.CreateClient();
        string token = AuthTestFactory.Provider.Mint(
            TestIdentity.Person.Subject, actions: Personas.AllActions, audience: "galley");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Get("/api/v1/servers", token))).StatusCode);
    }

    [Fact]
    public async Task An_expired_token_is_refused()
    {
        using HttpClient client = factory.CreateClient();
        string token = factory.AccessTokenWith(TestIdentity.Person, Personas.AllActions, TimeSpan.FromMinutes(-5));

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Get("/api/v1/servers", token))).StatusCode);
    }

    [Fact]
    public async Task A_token_that_is_not_an_access_token_is_refused()
    {
        using HttpClient client = factory.CreateClient();
        string idToken = AuthTestFactory.Provider.Mint(
            TestIdentity.Person.Subject, actions: Personas.AllActions, type: "JWT");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Get("/api/v1/servers", idToken))).StatusCode);
    }

    [Fact]
    public async Task A_token_signed_with_an_algorithm_the_provider_does_not_use_is_refused()
    {
        using HttpClient client = factory.CreateClient();
        string token = AuthTestFactory.Provider.Mint(TestIdentity.Person.Subject, actions: Personas.AllActions, rsa: true);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Get("/api/v1/servers", token))).StatusCode);
    }

    [Fact]
    public async Task No_token_is_the_ordinary_401_envelope()
    {
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/v1/servers");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("\"code\":\"unauthorized\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task What_a_token_lists_applies_on_the_next_token_and_not_before()
    {
        using HttpClient client = factory.CreateClient();
        TestPerson person = TestIdentity.PersonFor("renewal");
        string before = factory.AccessTokenFor(person, Persona.None);
        string after = factory.AccessTokenFor(person, Persona.Reader);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Get("/api/v1/hosts", before))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Get("/api/v1/hosts", after))).StatusCode);
    }

    [Fact]
    public async Task A_node_with_no_issuer_refuses_a_caller_beyond_its_own_machine()
    {
        using WebApplicationFactory<Program> node = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Sessions:Issuer"] = "" }));
            builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter>(new CallerFrom(IPAddress.Parse("203.0.113.9"))));
        });
        using HttpClient client = node.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/v1");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("\"code\":\"no_issuer\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_node_with_no_issuer_answers_its_own_machine()
    {
        using WebApplicationFactory<Program> node = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Sessions:Issuer"] = "" }));
            builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter>(new CallerFrom(IPAddress.Loopback)));
        });
        using HttpClient client = node.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/v1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>Gives every request the address a caller would have arrived from.</summary>
    private sealed class CallerFrom(IPAddress address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = address;
                return nextMiddleware(context);
            });
            next(app);
        };
    }

    [Fact]
    public async Task A_node_with_no_issuer_admits_no_person()
    {
        using WebApplicationFactory<Program> node = factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Sessions:Issuer"] = "" })));
        using HttpClient client = node.CreateClient();

        HttpResponseMessage response = await client.SendAsync(Get("/api/v1/servers", factory.AccessToken(Persona.Owner)));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
