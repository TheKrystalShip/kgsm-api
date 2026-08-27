using System.Net;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

using TheKrystalShip.Api;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Who an auth-disabled host says did the work.
/// </summary>
/// <remarks>
/// <para>
/// Turning auth off does not turn the audit log off: every request still lands in the record, and it
/// lands under whatever name the handler presents. A name compiled into the handler is a principal
/// nobody chose, and one carrying a provider it never authenticated against is worse — in the record
/// it is indistinguishable from a person who actually logged in through that provider.
/// </para>
/// <para>
/// So the name is configuration with no default, and the host refuses to build without a usable one.
/// Refusing at startup rather than at the first request is the point: the failure mode being
/// prevented is a host that runs perfectly well while mis-attributing everything it does.
/// </para>
/// </remarks>
public sealed class DisabledAuthIdentityTests(AuthTestFactory factory) : IClassFixture<AuthTestFactory>
{
    [Theory]
    [InlineData(null)]        // never set
    [InlineData("")]          // set to nothing
    [InlineData("dev")]       // a bare name, naming no provider
    [InlineData("discord:")]  // a provider claiming nobody
    [InlineData(":someone")]  // somebody from nowhere
    public void A_host_with_auth_off_and_no_usable_actor_refuses_to_start(string? actor)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Open(actor).CreateClient());

        Assert.Contains("Api__DisabledAuthActor", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_configured_actor_is_who_the_host_reports_the_caller_to_be()
    {
        using HttpResponseMessage me = await Open("local:claude").CreateClient().GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);

        using JsonDocument body = JsonDocument.Parse(await me.Content.ReadAsStringAsync());
        Assert.Equal("claude", body.RootElement.GetProperty("user").GetProperty("username").GetString());
    }

    /// <summary>
    /// The provider half is never invented. An actor from a provider this host has no application for
    /// still names its principal, and the open door is exactly where that happens — nothing about the
    /// request was verified against the provider in the first place.
    /// </summary>
    [Fact]
    public async Task An_actor_from_an_unconfigured_provider_is_still_accepted()
    {
        using HttpResponseMessage me = await Open("github:octocat").CreateClient().GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);

        using JsonDocument body = JsonDocument.Parse(await me.Content.ReadAsStringAsync());
        Assert.Equal("octocat", body.RootElement.GetProperty("user").GetProperty("username").GetString());
    }

    private WebApplicationFactory<Program> Open(string? actor) =>
        factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Api:AuthDisabled"] = "true",
                    ["Api:DisabledAuthActor"] = actor,
                    ["Api:DbPath"] = AuthTestFactory.NewDbPath("kgsm-api-tests-open-actor"),
                })));
}
