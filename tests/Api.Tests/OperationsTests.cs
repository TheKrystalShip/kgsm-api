using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Auth.Access;

using ActionIds = TheKrystalShip.Api.Services.Auth.ActionIds;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// <c>GET /api/v1/operations</c>: every gated route and the action it requires, built from what the
/// node enforces — the document a client gates on instead of a list of its own.
/// </summary>
public sealed class OperationsTests(AuthTestFactory factory) : IClassFixture<AuthTestFactory>
{
    private async Task<OperationManifest> ReadAsync()
    {
        HttpClient c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.AccessToken(Persona.None));
        HttpResponseMessage resp = await c.GetAsync("/api/v1/operations");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync(AccessJsonContext.Default.OperationManifest))!;
    }

    private static Operation One(OperationManifest m, string method, string route, string? value = null) =>
        Assert.Single(m.Operations, o => o.Method == method && o.Route == route && o.Value == value);

    [Fact]
    public async Task NoToken_401()
    {
        HttpResponseMessage resp = await factory.CreateClient().GetAsync("/api/v1/operations");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task AServerRoute_IsPublishedAtTheServersInstall()
    {
        OperationManifest m = await ReadAsync();

        Assert.Equal("/api/v1", m.Base);
        Operation backup = One(m, "POST", "/servers/{id}/backups");
        Assert.Equal(ActionIds.ServerBackupsCreate, backup.Action);
        Assert.Equal(Operation.Scopes.Instance, backup.Scope);
        Assert.Equal("id", backup.Target);

        Assert.Equal(Operation.Scopes.Node, One(m, "GET", "/alerts").Scope);
    }

    [Fact]
    public async Task ALifecycleCommand_IsOneEntryPerVerb_TheActionTheHandlerChecks()
    {
        OperationManifest m = await ReadAsync();

        foreach (string verb in ActionIds.Verbs)
        {
            Operation op = One(m, "POST", "/servers/{id}/commands", verb);
            Assert.Equal(ActionIds.ForVerb(verb), op.Action);
            Assert.Equal(ActionByVerbAttribute.Field, op.Field);
        }
    }

    [Fact]
    public async Task ALeafsConfiguration_NamesTheLeaf_AndTheEnginesIsItsOwn()
    {
        OperationManifest m = await ReadAsync();

        Assert.Equal("{leaf}:config.write", One(m, "PUT", "/hosts/{id}/services/{leaf}/config").Action);
        Assert.Equal(ActionIds.EngineConfigWrite, One(m, "PUT", "/hosts/{id}/services/kgsm/config").Action);
        Assert.Equal("{leaf}:config.read", One(m, "GET", "/hosts/{id}/services/{leaf}/config").Action);
    }

    [Fact]
    public async Task ASettingsChangeCarryingTheWindows_NeedsTheirActionBesideTheRoutes()
    {
        OperationManifest m = await ReadAsync();

        Operation[] patch = [.. m.Operations.Where(o => o.Method == "PATCH" && o.Route == "/servers/{id}/settings")];
        Assert.Contains(patch, o => o.Field is null && o.Action == ActionIds.ServerConfigWrite);
        Assert.Contains(patch, o => o is { Field: "maintenanceWindows", Value: null } && o.Action == ActionIds.ServerWindowsWrite);
    }

    [Fact]
    public async Task EveryActionGatedEndpoint_IsPublished()
    {
        OperationManifest m = await ReadAsync();
        EndpointDataSource endpoints = factory.Services.GetRequiredService<EndpointDataSource>();

        foreach (RouteEndpoint e in endpoints.Endpoints.OfType<RouteEndpoint>())
        {
            foreach (RequiresActionAttribute a in e.Metadata.GetOrderedMetadata<RequiresActionAttribute>())
            {
                string route = OperationManifest.NormalizeRoute(e.RoutePattern.RawText!)[m.Base.Length..];
                foreach (string method in e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
                {
                    Assert.Contains(m.Operations, o => o.Method == method && o.Route == route && o.Action == a.Action);
                }
            }
        }
    }
}
