using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// The vanishing-list fix: <c>GET /servers</c> must distinguish a FAILED engine read from a genuinely
/// empty roster. A transient kgsm read failure (<see cref="IInstanceService.GetAllOrNull"/> returns
/// <see langword="null"/>) → <c>503 engine_unavailable</c>, so the SPA keeps its last-known list instead
/// of wiping it from a <c>200 []</c>. A successful read of zero instances (an empty dict — kgsm emits
/// <c>{}</c>) → a normal <c>200 []</c> (honestly zero servers). The two must never be conflated.
/// </summary>
public sealed class ServerListReadFailureTests
{
    [Fact]
    public async Task FailedRosterRead_returns_503_not_an_empty_200()
    {
        await using var factory = new RosterFactory(roster: null);   // GetAllOrNull → null = read FAILED
        HttpResponseMessage res = await Viewer(factory).GetAsync("/api/v1/servers");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        ErrorEnvelope? body = await res.Content.ReadFromJsonAsync<ErrorEnvelope>();
        Assert.Equal("engine_unavailable", body?.Error?.Code);
    }

    [Fact]
    public async Task GenuinelyEmptyRoster_returns_200_empty_list()
    {
        await using var factory = new RosterFactory(roster: new());  // GetAllOrNull → {} = genuinely zero
        HttpResponseMessage res = await Viewer(factory).GetAsync("/api/v1/servers");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("[]", (await res.Content.ReadAsStringAsync()).Trim());
    }

    private static HttpClient Viewer(AuthTestFactory factory)
    {
        HttpClient c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", factory.AccessToken(AuthTier.Viewer));
        return c;
    }

    private sealed record ErrorEnvelope(ErrorBody? Error);
    private sealed record ErrorBody(string? Code, string? Message);

    /// <summary>The real pipeline with a roster-controllable <see cref="IInstanceService"/> swapped in.</summary>
    private sealed class RosterFactory(Dictionary<string, Instance>? roster) : AuthTestFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IInstanceService>();
                services.AddSingleton<IInstanceService>(new RosterFake(roster));
            });
        }
    }

    /// <summary>
    /// Switch-on-construction fake: <see cref="GetAllOrNull"/> returns the configured roster verbatim
    /// (null models a failed read; an empty dict models a genuine zero roster). Everything the
    /// <c>GET /servers</c> path doesn't touch throws — never silently fabricate.
    /// </summary>
    private sealed class RosterFake(Dictionary<string, Instance>? roster) : IInstanceService
    {
        public Dictionary<string, Instance>? GetAllOrNull() => roster;
        public Dictionary<string, Instance> GetAll() => roster ?? new();
        public Dictionary<string, Reading<InstanceRuntimeStatus>> GetAllStatuses(bool fast = false) => new();

        // --- untouched by GET /servers: honest NotImplemented (never silently fabricate) ---
        public KgsmResult GenerateId(string blueprintName, string? customName = null) => throw new NotImplementedException();
        public KgsmResult Install(string blueprintName, string? installDir = null, string? version = null, string? name = null, string? actor = null, string? origin = null, int? port = null) => throw new NotImplementedException();
        public KgsmResult Uninstall(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public Instance? GetInstanceInfo(string instanceName) => throw new NotImplementedException();
        public InstanceRuntimeStatus? GetInstanceStatus(string instanceName) => throw new NotImplementedException();
        public ICollection<string> GetLogs(string instanceName, int maxLines = 10) => throw new NotImplementedException();
        public Task<ICollection<string>> GetLogsAsync(string instanceName, int maxLines = 10, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public KgsmResult GetStatus(string instanceName) => throw new NotImplementedException();
        public KgsmResult GetInfo(string instanceName) => throw new NotImplementedException();
        public bool IsActive(string instanceName) => throw new NotImplementedException();
        public KgsmResult Start(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Stop(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Restart(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult GetInstalledVersion(string instanceName) => throw new NotImplementedException();
        public KgsmResult GetLatestVersion(string instanceName) => throw new NotImplementedException();
        public KgsmResult CheckUpdate(string instanceName) => throw new NotImplementedException();
        public KgsmResult Update(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult GetBackups(string instanceName) => throw new NotImplementedException();
        public KgsmResult CreateBackup(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult RestoreBackup(string instanceName, string backupName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Save(string instanceName) => throw new NotImplementedException();
        public KgsmResult SendInput(string instanceName, string command, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult FindConfigPath(string instanceName) => throw new NotImplementedException();
        public KgsmResult GetInstanceConfigValue(string instanceName, string key) => throw new NotImplementedException();
        public KgsmResult SetInstanceConfigValue(string instanceName, string key, string value, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public Task<LogSubscription> SubscribeToLogsAsync(string instanceName, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<LogSubscription> SubscribeToLogsAsync(string instanceName, LogLevel minimumLogLevel, bool includeRawLines = true, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}
