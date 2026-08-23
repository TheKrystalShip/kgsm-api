using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// <c>DELETE /hosts/{id}/libraries/{name}</c> and its <c>?drain=</c> — the sanctioned way a disk is
/// emptied before it is taken out.
/// </summary>
/// <remarks>
/// ⚠ <b>There is still no force.</b> The engine refuses a library that holds instances, naming them, and
/// this API adds no pass-through — a panel button that deregistered a populated root would produce, in
/// one click, the state the engine exists to prevent. Draining is the alternative, and it is what these
/// pin: the target reaches the engine as given, the refusals come back in the engine's own words, and a
/// library cannot be drained into itself.
/// </remarks>
public sealed class HostLibraryDrainTests : IClassFixture<HostLibraryDrainTests.LibraryFactory>
{
    private readonly LibraryFactory _factory;

    public HostLibraryDrainTests(LibraryFactory factory) => _factory = factory;

    [Fact]
    public async Task Remove_WithDrain_PassesTheTargetToTheEngine()
    {
        HttpResponseMessage resp = await Delete(KgsmTier.Admin, "ssd", "?drain=archive");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(("ssd", "archive", false), _factory.Registry.LastRemove);
    }

    [Fact]
    public async Task Remove_WithoutDrain_SendsNoTarget()
    {
        HttpResponseMessage resp = await Delete(KgsmTier.Admin, "ssd");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(("ssd", null, false), _factory.Registry.LastRemove);
    }

    [Fact]
    public async Task Remove_DrainingIntoItself_400_WithoutReachingTheEngine()
    {
        _factory.Registry.LastRemove = null;

        HttpResponseMessage resp = await Delete(KgsmTier.Admin, "ssd", "?drain=ssd");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Null(_factory.Registry.LastRemove);
    }

    [Fact]
    public async Task Remove_RefusedByTheEngine_409_InItsOwnWords()
    {
        // The refusal names the running instances that blocked the drain, and no wording composed here
        // could carry that. 409, not 400: the request is fine and the removal will work once they stop.
        HttpResponseMessage resp = await Delete(KgsmTier.Admin, "busy", "?drain=archive");

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("holds instances that are running", body, StringComparison.Ordinal);
        Assert.Contains("factorio-1", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remove_Operator_403()
    {
        HttpResponseMessage resp = await Delete(KgsmTier.Operator, "ssd", "?drain=archive");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // --- helpers -----------------------------------------------------------------------------------

    private Task<HttpResponseMessage> Delete(KgsmTier tier, string name, string query = "")
    {
        HttpClient c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.AccessToken(tier));
        return c.DeleteAsync($"/api/v1/hosts/{TestHostId}/libraries/{name}{query}");
    }

    // The controller answers 404 for any other host id — this API aggregates its own host only.
    private const string TestHostId = "test-host";

    public sealed class LibraryFactory : AuthTestFactory
    {
        public RecordingLibraryService Registry { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Api:HostId", TestHostId);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ILibraryService>();
                services.AddSingleton<ILibraryService>(Registry);
            });
        }
    }

    /// <summary>Records what reached the engine, and refuses the one library holding a running instance.</summary>
    public sealed class RecordingLibraryService : ILibraryService
    {
        public (string Name, string? DrainTo, bool Force)? LastRemove { get; set; }

        public List<Library>? List() =>
        [
            new() { Name = "ssd", Path = "/mnt/ssd", State = LibraryState.Online },
            new() { Name = "archive", Path = "/mnt/archive", State = LibraryState.Online },
        ];

        public KgsmResult Remove(
            string name, bool force = false, string? drainTo = null, string? actor = null, string? origin = null)
        {
            LastRemove = (name, drainTo, force);
            return string.Equals(name, "busy", StringComparison.Ordinal)
                ? new KgsmResult(57, "",
                    "[ERROR] Library 'busy' holds instances that are running:\n[ERROR]   factorio-1\n"
                    + "[ERROR] Stop them and run the drain again; nothing has been moved")
                : new KgsmResult(0, "Deregistered library");
        }

        public KgsmResult Add(string path, string? name = null, string? actor = null, string? origin = null) =>
            throw new NotImplementedException();

        public KgsmResult Rename(string oldName, string newName, string? actor = null, string? origin = null) =>
            throw new NotImplementedException();
    }
}
