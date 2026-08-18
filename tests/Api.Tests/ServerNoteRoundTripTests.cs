using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;

using TheKrystalShip.KGSM.Auth;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// The note's storage round trip, against a REAL bash-sourced config file.
/// <para>
/// A note lives in the kgsm instance's own <c>.config.ini</c>, and that file is <b>sourced by bash</b>
/// — so a body carrying <c>"</c>, <c>$</c>, a backtick or a newline is not a formatting nicety, it is
/// the difference between a note and a broken (or expanded) config. <see cref="ServerNoteTests"/>
/// records note writes into memory, which cannot see that class of bug at all; this suite closes it by
/// giving the fake engine a real file and reading the value back the way kgsm does — by sourcing it.
/// </para>
/// <para>
/// Base64 is what makes the body inert in that position, so the last test writes an <em>unencoded</em>
/// body through the same path to show the mangling the encoding prevents. It uses variable expansion,
/// never command substitution: the point is provable without a test that runs a shell command out of
/// its own fixture data.
/// </para>
/// </summary>
public sealed class ServerNoteRoundTripTests : IClassFixture<ServerNoteRoundTripTests.SourcedConfigFactory>
{
    private readonly SourcedConfigFactory _factory;

    public ServerNoteRoundTripTests(SourcedConfigFactory factory) => _factory = factory;

    /// <summary>Bodies whose characters are load-bearing in a sourced file.</summary>
    public static TheoryData<string, string> HostileBodies() => new()
    {
        { "quotes-dollars-ticks-newline", "smoke: mods v1 — quotes \" $dollars `ticks`\nand a second line" },
        { "double-quote-run", "he said \"no griefing\" and meant it" },
        { "shell-expansion-shaped", "Rules live in $HOME/rules.txt — ${NOT_A_VAR} stays literal" },
        { "command-substitution-shaped", "backup names use $(date +%F) and `hostname` — keep them literal" },
        { "config-line-shaped", "note=\"already set\"\nport=9999" },
        { "backslashes-and-unicode", "path C:\\servers\\mc — 日本語 — emoji 🎮" },
        { "leading-hash-comment", "# this is not a comment, it is the note" },
    };

    /// <summary>
    /// Fixture bodies reach a file that gets <c>source</c>d, so one carrying command substitution is
    /// executable in any path where the encoding is absent. <see cref="InstanceNote.Encode"/> and the
    /// emptied PATH in <see cref="SourcedConfigFactory.SourceKey"/> are what make it inert; this holds
    /// the fixture data itself to commands that change nothing, so the suite stays harmless even with
    /// both of those out of the way. It is a denylist of destructive verbs, not a sandbox.
    /// </summary>
    [Fact]
    public void FixtureBodiesNameNoDestructiveCommand()
    {
        string[] destructive = ["rm -", "rmdir", "reboot", "shutdown", "poweroff", "mkfs", "dd if=", ":(){"];

        foreach (object[] row in HostileBodies())
        {
            var label = (string)row[0];
            var body = (string)row[1];
            foreach (string verb in destructive)
                Assert.False(
                    body.Contains(verb, StringComparison.OrdinalIgnoreCase),
                    $"fixture '{label}' names '{verb.Trim()}' — a sourced file would run it");
        }
    }

    [Theory]
    [MemberData(nameof(HostileBodies))]
    public async Task Put_ThenGet_BodySurvivesASourcedConfigVerbatim(string label, string body)
    {
        _ = label; // names the case in test output

        HttpResponseMessage put = await Put(KgsmTier.Operator, SourcedConfigFactory.Instance,
            JsonSerializer.Serialize(new { body, origin = "ui" }));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        // The GET re-reads through the fake's `source`-the-file path, so this is the byte trip:
        // encode → key="value" in a real file → bash source → decode.
        HttpResponseMessage get = await Get(KgsmTier.Viewer, SourcedConfigFactory.Instance);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        Assert.Equal(body, doc.RootElement.GetProperty("note").GetProperty("body").GetString());
    }

    [Fact]
    public async Task Put_ThenGet_AttributionSurvivesAlongsideTheBody()
    {
        // The two attribution keys share the file with the body; a body that broke the sourcing would
        // take them down with it, so asserting they come back proves the whole file still parses.
        HttpResponseMessage put = await Put(KgsmTier.Operator, SourcedConfigFactory.Instance,
            "{\"body\":\"quotes \\\" and $vars\",\"origin\":\"ui\"}");
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(
            await (await Get(KgsmTier.Viewer, SourcedConfigFactory.Instance)).Content.ReadAsStringAsync());
        JsonElement note = doc.RootElement.GetProperty("note");
        Assert.Equal("quotes \" and $vars", note.GetProperty("body").GetString());
        Assert.False(string.IsNullOrEmpty(note.GetProperty("updatedBy").GetString()));
        Assert.False(string.IsNullOrEmpty(note.GetProperty("updatedAt").GetString()));
    }

    [Fact]
    public async Task Put_ThenList_TheServerListDtoCarriesTheSameDecodedBody()
    {
        // The dashboard tile reads the note off the roster, not the detail endpoint — same file, same
        // sourcing, and it must decode identically on both paths.
        const string body = "Modpack \"v3\" — $10 entry fee";
        Assert.Equal(HttpStatusCode.OK,
            (await Put(KgsmTier.Operator, SourcedConfigFactory.Instance,
                JsonSerializer.Serialize(new { body, origin = "ui" }))).StatusCode);

        HttpResponseMessage list = await Client(KgsmTier.Viewer).GetAsync("/api/v1/servers");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        JsonElement row = doc.RootElement.EnumerateArray()
            .First(s => s.GetProperty("id").GetString() == SourcedConfigFactory.Instance);
        Assert.Equal(body, row.GetProperty("note").GetProperty("body").GetString());
    }

    [Fact]
    public async Task Delete_ClearsTheBody_AndTheFileStillSources()
    {
        Assert.Equal(HttpStatusCode.OK,
            (await Put(KgsmTier.Operator, SourcedConfigFactory.Instance,
                "{\"body\":\"about to be cleared \\\"quoted\\\"\",\"origin\":\"ui\"}")).StatusCode);

        HttpResponseMessage del = await Client(KgsmTier.Operator)
            .DeleteAsync($"/api/v1/servers/{SourcedConfigFactory.Instance}/note");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(
            await (await Get(KgsmTier.Viewer, SourcedConfigFactory.Instance)).Content.ReadAsStringAsync());
        // Honestly null (nothing written), and reachable at all only because the file still parses.
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("note").ValueKind);
    }

    [Fact]
    public void AnUnencodedBodyIsMangledByTheSourcing()
    {
        // Why the encoding exists. The same file, the same `source`, one difference: the body written
        // raw instead of base64. bash expands `$HOME` inside the double-quoted value, so what comes
        // back is not what was written — the note silently becomes a different sentence.
        const string body = "Rules live in $HOME/rules.txt";
        string path = Path.Combine(Path.GetTempPath(), $"kgsm-note-raw-{Guid.NewGuid():N}.config.ini");
        try
        {
            File.WriteAllText(path, $"name=\"probe\"\nnote=\"{body}\"\n");
            string sourced = SourcedConfigFactory.SourceKey(path, "note");

            Assert.NotEqual(body, sourced);
            Assert.DoesNotContain("$HOME", sourced, StringComparison.Ordinal);

            // And the encoded form of the same body, in the same position, comes back untouched.
            File.WriteAllText(path, $"name=\"probe\"\nnote=\"{InstanceNote.Encode(body)}\"\n");
            Assert.Equal(body, InstanceNote.Decode(SourcedConfigFactory.SourceKey(path, "note")));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // --- helpers ---------------------------------------------------------------------------------

    private HttpClient Client(KgsmTier tier)
    {
        HttpClient c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _factory.AccessToken(tier));
        return c;
    }

    private Task<HttpResponseMessage> Get(KgsmTier tier, string id) =>
        Client(tier).GetAsync($"/api/v1/servers/{id}/note");

    private Task<HttpResponseMessage> Put(KgsmTier tier, string id, string json) =>
        Client(tier).PutAsync($"/api/v1/servers/{id}/note",
            new StringContent(json, Encoding.UTF8, "application/json"));

    /// <summary>
    /// <see cref="AuthTestFactory"/> whose <see cref="IInstanceService"/> keeps the note in a real
    /// <c>.config.ini</c> on disk and reads it back by sourcing that file — the one thing an in-memory
    /// fake cannot reproduce.
    /// </summary>
    public sealed class SourcedConfigFactory : AuthTestFactory, IDisposable
    {
        public const string Instance = "sourced";

        public string ConfigPath { get; } =
            Path.Combine(Path.GetTempPath(), $"kgsm-note-roundtrip-{Guid.NewGuid():N}.config.ini");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            File.WriteAllText(ConfigPath, $"name=\"{Instance}\"\nblueprint_file=\"factorio.bp.yaml\"\n");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IInstanceService>();
                services.AddSingleton<IInstanceService>(new SourcedConfigInstanceService(ConfigPath));
                services.RemoveAll<IWatchdogClient>();
            });
        }

        /// <summary>
        /// Read one key out of a kgsm config file the way kgsm does — by sourcing it in bash. The value
        /// is printed with a NUL terminator so a body containing newlines survives the capture.
        /// </summary>
        public static string SourceKey(string configPath, string key)
        {
            // bash is named by absolute path because the child's PATH is emptied below.
            string bash = File.Exists("/usr/bin/bash") ? "/usr/bin/bash" : "/bin/bash";

            var psi = new ProcessStartInfo(bash)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            // This helper sources a file built from fixture data, so a body carrying command
            // substitution is executable the moment it reaches that file unencoded. An empty PATH
            // means no external binary resolves, so such a substitution expands to nothing instead
            // of running. `source` and `printf` are builtins and are unaffected. HOME stays intact —
            // AnUnencodedBodyIsMangledByTheSourcing asserts on its expansion.
            psi.Environment["PATH"] = "";

            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add($"set -a; source \"$1\"; printf '%s' \"${{{key}-}}\"");
            psi.ArgumentList.Add("kgsm-note-test");   // $0
            psi.ArgumentList.Add(configPath);          // $1

            using Process p = Process.Start(psi)
                ?? throw new InvalidOperationException("could not start bash");
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(10_000);

            if (p.ExitCode != 0)
                throw new InvalidOperationException(
                    $"sourcing {configPath} failed (exit {p.ExitCode}): {stderr}");

            return stdout;
        }

        public new void Dispose()
        {
            try { File.Delete(ConfigPath); } catch (IOException) { /* best effort */ }
            base.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// An <see cref="IInstanceService"/> backed by a real config file: a note write encodes and
    /// rewrites the three keys in kgsm's own <c>key="value"</c> form, and every read sources the file.
    /// Writes are serialized so a parallel-running test class can never observe a half-written file.
    /// </summary>
    private sealed class SourcedConfigInstanceService(string configPath) : IInstanceService
    {
        private readonly Lock _gate = new();

        public InstanceNoteResult SetInstanceNote(string instanceName, string body, string? actor = null, string? origin = null)
        {
            if (instanceName != SourcedConfigFactory.Instance)
                return new InstanceNoteResult(false, [], InstanceNote.BodyKey, "unknown instance", 1);

            lock (_gate)
            {
                // kgsm writes one key per call; mirroring that ordering keeps the applied-keys list
                // (and therefore the partial-failure contract) honest.
                Set(InstanceNote.UpdatedByKey, actor ?? "");
                Set(InstanceNote.UpdatedAtKey, DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
                Set(InstanceNote.BodyKey, InstanceNote.Encode(body));
            }

            return new InstanceNoteResult(true,
                [InstanceNote.UpdatedByKey, InstanceNote.UpdatedAtKey, InstanceNote.BodyKey]);
        }

        // Rewrite one key in place (or append it), quoted the way kgsm quotes config values.
        private void Set(string key, string value)
        {
            string[] lines = File.ReadAllLines(configPath);
            string line = $"{key}=\"{value}\"";
            int at = Array.FindIndex(lines, l => l.StartsWith(key + "=", StringComparison.Ordinal));
            File.WriteAllLines(configPath, at >= 0 ? [.. lines[..at], line, .. lines[(at + 1)..]] : [.. lines, line]);
        }

        private Instance Read()
        {
            lock (_gate)
            {
                return new Instance
                {
                    Name = SourcedConfigFactory.Instance,
                    BlueprintFile = "factorio.bp.yaml",
                    Note = SourcedConfigFactory.SourceKey(configPath, InstanceNote.BodyKey),
                    NoteUpdatedBy = SourcedConfigFactory.SourceKey(configPath, InstanceNote.UpdatedByKey),
                    NoteUpdatedAt = SourcedConfigFactory.SourceKey(configPath, InstanceNote.UpdatedAtKey),
                };
            }
        }

        public Dictionary<string, Instance>? GetAllOrNull() => GetAll();

        public Dictionary<string, Instance> GetAll() => new() { [SourcedConfigFactory.Instance] = Read() };

        public Instance? GetInstanceInfo(string instanceName) =>
            instanceName == SourcedConfigFactory.Instance ? Read() : null;

        public Dictionary<string, Reading<InstanceRuntimeStatus>> GetAllStatuses(bool fast = false) => new();

        // --- unused by the note path: honest NotImplemented (never silently fabricate) ---
        public KgsmResult SetInstanceConfigValue(string instanceName, string key, string value, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public InstanceRuntimeStatus? GetInstanceStatus(string instanceName) => throw new NotImplementedException();
        public ICollection<string> GetLogs(string instanceName, int maxLines = 10) => throw new NotImplementedException();
        public Task<ICollection<string>> GetLogsAsync(string instanceName, int maxLines = 10, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public KgsmResult GetStatus(string instanceName) => throw new NotImplementedException();
        public KgsmResult GetInfo(string instanceName) => throw new NotImplementedException();
        public bool IsActive(string instanceName) => throw new NotImplementedException();
        public KgsmResult GenerateId(string blueprintName, string? customName = null) => throw new NotImplementedException();
        public KgsmResult Install(string blueprintName, string? installDir = null, string? version = null, string? name = null, string? actor = null, string? origin = null, int? port = null, bool? start = null) => throw new NotImplementedException();
        public KgsmResult Uninstall(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Start(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Stop(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Restart(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult GetInstalledVersion(string instanceName) => throw new NotImplementedException();
        public KgsmResult GetLatestVersion(string instanceName) => throw new NotImplementedException();
        public KgsmResult CheckUpdate(string instanceName, bool emit = false, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Update(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult GetBackups(string instanceName) => throw new NotImplementedException();
        public List<InstanceBackup> GetBackupsDetailed(string instanceName) => throw new NotImplementedException();
        public KgsmResult CreateBackup(string instanceName, string? actor = null, string? origin = null, string? reason = null, string? retention = null) => throw new NotImplementedException();
        public KgsmResult PinBackup(string instanceName, string backupName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult UnpinBackup(string instanceName, string backupName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public List<InstanceConfigEntry>? GetInstanceConfig(string instanceName, bool settableOnly = false) => throw new NotImplementedException();
        public KgsmResult RestoreBackup(string instanceName, string backupName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult DeleteBackup(string instanceName, string backupName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult PruneBackups(string instanceName, int keepN, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Save(string instanceName) => throw new NotImplementedException();
        public KgsmResult SendInput(string instanceName, string command, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Kick(string instanceName, string target, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Ban(string instanceName, string target, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Unban(string instanceName, string target, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult FindConfigPath(string instanceName) => throw new NotImplementedException();
        public KgsmResult GetInstanceConfigValue(string instanceName, string key) => throw new NotImplementedException();
        public Task<LogSubscription> SubscribeToLogsAsync(string instanceName, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<LogSubscription> SubscribeToLogsAsync(string instanceName, LogLevel minimumLogLevel, bool includeRawLines = true, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}
