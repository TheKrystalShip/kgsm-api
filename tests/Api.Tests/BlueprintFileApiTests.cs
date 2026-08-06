using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

using TheKrystalShip.KGSM.Auth;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// HTTP-contract + auth coverage for the library blueprint editor
/// (<c>GET/PUT/DELETE /library/{id}/file</c>), proven through the real pipeline with the kgsm-lib
/// blueprint seam faked against an in-memory pair of blueprint directories. The load-bearing assertions:
/// the SPLIT gate (operator reads, admin writes — a viewer cannot even read, an operator gets
/// <c>readOnly:true</c>), the override lifecycle (editing a shipped blueprint reports
/// <c>createdOverride</c>, reverting it is allowed, reverting a user-only one is <c>409 no_original</c>),
/// the engine's validation errors surfacing verbatim as <c>400 blueprint_invalid</c>, and the etag
/// <c>412</c>.
/// </summary>
public sealed class BlueprintFileApiTests
    : IClassFixture<BlueprintFileApiTests.BlueprintTestFactory>, IClassFixture<AuthTestFactory>
{
    // Shipped-only: the case that exercises the override path (a save creates a user copy).
    private const string Shipped = "factorio";
    // Both dirs: already an override, so revert is meaningful.
    private const string Overridden = "palworld";
    // User-only: revert would destroy the only copy → refused.
    private const string UserOnly = "teamfortress2";

    private readonly BlueprintTestFactory _engine;
    private readonly AuthTestFactory _noEngine;

    public BlueprintFileApiTests(BlueprintTestFactory engine, AuthTestFactory noEngine)
    {
        _engine = engine;
        _noEngine = noEngine;
        engine.Blueprints.Reset();
    }

    // ===== auth gate (operator reads, admin writes) ================================================

    [Fact]
    public async Task Read_NoToken_401()
    {
        HttpResponseMessage r = await Client(_engine, tier: null).GetAsync($"/api/v1/library/{Shipped}/file");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Read_Viewer_403_ReadIsOperatorPlus()
    {
        // The catalog listing is viewer-gated, but the FILE is the engine's operational definition of how
        // a server is launched — operator+, tightening the class-level policy.
        HttpResponseMessage r = await Client(_engine, KgsmTier.Viewer).GetAsync($"/api/v1/library/{Shipped}/file");
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task Save_Operator_403_WritesAreAdminOnly()
    {
        HttpResponseMessage r = await Put(_engine, KgsmTier.Operator, Shipped, Body("name: factorio\n"));
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task Revert_Operator_403_WritesAreAdminOnly()
    {
        HttpResponseMessage r = await Client(_engine, KgsmTier.Operator)
            .DeleteAsync($"/api/v1/library/{Overridden}/file");
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task Read_Operator_200_ButReadOnly()
    {
        JsonElement body = await ReadOk(KgsmTier.Operator, Shipped);
        // An operator may open the editor and may not save — one honest flag, not a hidden 403 on submit.
        Assert.True(body.GetProperty("readOnly").GetBoolean());
    }

    [Fact]
    public async Task Read_Admin_200_NotReadOnly()
    {
        JsonElement body = await ReadOk(KgsmTier.Admin, Shipped);
        Assert.False(body.GetProperty("readOnly").GetBoolean());
    }

    // ===== degrade ================================================================================

    [Fact]
    public async Task Read_EngineUnprovisioned_503()
    {
        HttpResponseMessage r = await Client(_noEngine, KgsmTier.Operator).GetAsync($"/api/v1/library/{Shipped}/file");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
    }

    [Fact]
    public async Task Read_UnknownBlueprint_404()
    {
        HttpResponseMessage r = await Client(_engine, KgsmTier.Operator).GetAsync("/api/v1/library/nope/file");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    // ===== read (tier + override state) ============================================================

    [Fact]
    public async Task Read_ShippedBlueprint_ReportsSystemTierAndNoOverride()
    {
        JsonElement body = await ReadOk(KgsmTier.Admin, Shipped);

        Assert.Equal(Shipped, body.GetProperty("name").GetString());
        Assert.Equal("system", body.GetProperty("tier").GetString());
        Assert.False(body.GetProperty("overridesSystem").GetBoolean());
        // No user copy shadowing it yet → nothing to revert TO... nothing to revert FROM, rather.
        Assert.False(body.GetProperty("canRevert").GetBoolean());
        Assert.Equal("utf-8", body.GetProperty("encoding").GetString());
        Assert.StartsWith("sha256:", body.GetProperty("etag").GetString());
        Assert.Equal(AuthTestFactory.HostId, body.GetProperty("hostId").GetString());
    }

    [Fact]
    public async Task Read_PreservesTheFileVerbatim()
    {
        // The whole reason this surface reads raw bytes: comments and ordering must survive, which a typed
        // parse-and-re-render round-trip would destroy.
        JsonElement body = await ReadOk(KgsmTier.Admin, Shipped);
        Assert.Equal(BlueprintTestFactory.ShippedContent, body.GetProperty("content").GetString());
    }

    [Fact]
    public async Task Read_OverriddenBlueprint_ReportsUserTierAndOverride()
    {
        JsonElement body = await ReadOk(KgsmTier.Admin, Overridden);

        Assert.Equal("user", body.GetProperty("tier").GetString());
        Assert.True(body.GetProperty("overridesSystem").GetBoolean());
        Assert.True(body.GetProperty("canRevert").GetBoolean());
    }

    [Fact]
    public async Task Read_UserOnlyBlueprint_IsUserTierButNotAnOverride()
    {
        JsonElement body = await ReadOk(KgsmTier.Admin, UserOnly);

        Assert.Equal("user", body.GetProperty("tier").GetString());
        Assert.False(body.GetProperty("overridesSystem").GetBoolean());
        // The §0.7 rule, surfaced: there is no shipped original to fall back to.
        Assert.False(body.GetProperty("canRevert").GetBoolean());
    }

    [Fact]
    public async Task Read_RuntimeIsNullWhenTheBlueprintIsNotInTheCatalog()
    {
        // The engine's catalog is the only runtime source; this factory has no catalog, so the field is
        // honestly null rather than parsed out of the file content.
        JsonElement body = await ReadOk(KgsmTier.Admin, Shipped);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("runtime").ValueKind);
    }

    // ===== save ===================================================================================

    [Fact]
    public async Task Save_ShippedBlueprint_CreatesAnOverride()
    {
        HttpResponseMessage r = await Put(_engine, KgsmTier.Admin, Shipped, Body("name: factorio\n# edited\n"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        JsonElement body = await Json(r);
        // A write ALWAYS lands in the user dir — the shipped file is structurally unreachable.
        Assert.Equal("user", body.GetProperty("tier").GetString());
        Assert.True(body.GetProperty("overridesSystem").GetBoolean());
        Assert.True(body.GetProperty("createdOverride").GetBoolean()); // THIS save started the shadowing
        Assert.StartsWith("sha256:", body.GetProperty("etag").GetString());

        // The shipped file itself is untouched; the edit lives in the user dir.
        Assert.Equal(BlueprintTestFactory.ShippedContent, _engine.Blueprints.System[Shipped]);
        Assert.Contains("# edited", _engine.Blueprints.User[Shipped]);
    }

    [Fact]
    public async Task Save_AnExistingOverride_IsNotACreatedOverride()
    {
        HttpResponseMessage r = await Put(_engine, KgsmTier.Admin, Overridden, Body("name: palworld\n# again\n"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        JsonElement body = await Json(r);
        Assert.True(body.GetProperty("overridesSystem").GetBoolean()); // still shadowing
        Assert.False(body.GetProperty("createdOverride").GetBoolean()); // but this save didn't start it
    }

    [Fact]
    public async Task Save_UserOnlyBlueprint_OverridesNothing()
    {
        HttpResponseMessage r = await Put(_engine, KgsmTier.Admin, UserOnly, Body("name: teamfortress2\n"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        JsonElement body = await Json(r);
        Assert.False(body.GetProperty("overridesSystem").GetBoolean());
        Assert.False(body.GetProperty("createdOverride").GetBoolean());
    }

    [Fact]
    public async Task Save_WritesTheContentVerbatim()
    {
        const string content = "# a comment the engine keeps\nname: factorio\nruntime: native\n";
        await Put(_engine, KgsmTier.Admin, Shipped, Body(content));
        Assert.Equal(content, _engine.Blueprints.User[Shipped]);
    }

    [Fact]
    public async Task Save_EngineRejectsIt_400_WithTheEnginesOwnErrors()
    {
        _engine.Blueprints.RejectWith = ["missing required field: native.executable_file",
                                         "runtime must be one of: native, container"];

        HttpResponseMessage r = await Put(_engine, KgsmTier.Admin, Shipped, Body("name: broken\n"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);

        JsonElement error = (await Json(r)).GetProperty("error");
        Assert.Equal("blueprint_invalid", error.GetProperty("code").GetString());
        // The engine's messages ride through verbatim as a LIST — the API neither rewords them nor
        // re-implements the schema check that produced them.
        string[] errors = [.. error.GetProperty("details").GetProperty("errors")
            .EnumerateArray().Select(e => e.GetString()!)];
        Assert.Equal(
            ["missing required field: native.executable_file", "runtime must be one of: native, container"],
            errors);

        // Nothing was written.
        Assert.False(_engine.Blueprints.User.ContainsKey(Shipped));
    }

    [Fact]
    public async Task Save_StaleEtag_412()
    {
        HttpResponseMessage r = await Put(_engine, KgsmTier.Admin, Shipped,
            JsonSerializer.Serialize(new { content = "name: factorio\n", etag = "sha256:deadbeef" }));
        Assert.Equal(HttpStatusCode.PreconditionFailed, r.StatusCode);
        Assert.False(_engine.Blueprints.User.ContainsKey(Shipped));
    }

    [Fact]
    public async Task Save_CurrentEtag_200()
    {
        JsonElement read = await ReadOk(KgsmTier.Admin, Shipped);
        string etag = read.GetProperty("etag").GetString()!;

        HttpResponseMessage r = await Put(_engine, KgsmTier.Admin, Shipped,
            JsonSerializer.Serialize(new { content = "name: factorio\n# ok\n", etag }));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Save_MissingContent_400()
    {
        HttpResponseMessage r = await Put(_engine, KgsmTier.Admin, Shipped, "{}");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Save_BadOrigin_400()
    {
        HttpResponseMessage r = await Put(_engine, KgsmTier.Admin, Shipped,
            JsonSerializer.Serialize(new { content = "x", origin = "hacker" }));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Save_ThreadsTheActorAndOriginIntoTheEngineWrite()
    {
        // Without this the kgsm event — and therefore the echoed audit row — would attribute an admin's
        // browser edit to the service account. There is no direct audit write to check instead: the row
        // comes back through the engine echo, so the provenance has to ride the emit.
        await Put(_engine, KgsmTier.Admin, Shipped,
            JsonSerializer.Serialize(new { content = "name: factorio\n", origin = "ui" }));

        Assert.Equal("ui", _engine.Blueprints.LastWriteOrigin);
        Assert.False(string.IsNullOrEmpty(_engine.Blueprints.LastWriteActor));
    }

    // ===== revert =================================================================================

    [Fact]
    public async Task Revert_AnOverride_200_AndTheShippedFileServesAgain()
    {
        HttpResponseMessage r = await Client(_engine, KgsmTier.Admin).DeleteAsync($"/api/v1/library/{Overridden}/file");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        JsonElement body = await Json(r);
        Assert.Equal("system", body.GetProperty("revertedTo").GetString());

        Assert.False(_engine.Blueprints.User.ContainsKey(Overridden)); // the override is gone
        Assert.True(_engine.Blueprints.System.ContainsKey(Overridden)); // the shipped one never moved
    }

    [Fact]
    public async Task Revert_UserOnlyBlueprint_409_NoOriginal()
    {
        // §0.7 enforced server-side, independently of the SPA hiding the button: there is nothing to
        // revert TO, so this would be a deletion of the only copy masquerading as a revert.
        HttpResponseMessage r = await Client(_engine, KgsmTier.Admin).DeleteAsync($"/api/v1/library/{UserOnly}/file");
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);

        JsonElement error = (await Json(r)).GetProperty("error");
        Assert.Equal("no_original", error.GetProperty("code").GetString());
        Assert.True(_engine.Blueprints.User.ContainsKey(UserOnly)); // still there
    }

    [Fact]
    public async Task Revert_ShippedOnlyBlueprint_404_NothingToRemove()
    {
        HttpResponseMessage r = await Client(_engine, KgsmTier.Admin).DeleteAsync($"/api/v1/library/{Shipped}/file");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Revert_ThreadsTheActorAndOriginIntoTheEngineRemove()
    {
        await Client(_engine, KgsmTier.Admin).DeleteAsync($"/api/v1/library/{Overridden}/file?origin=ui");

        Assert.Equal("ui", _engine.Blueprints.LastRemoveOrigin);
        Assert.False(string.IsNullOrEmpty(_engine.Blueprints.LastRemoveActor));
    }

    // ===== scaffold ===============================================================================

    [Fact]
    public async Task Scaffold_Viewer_403_OperatorPlus()
    {
        HttpResponseMessage r = await Client(_engine, KgsmTier.Viewer).GetAsync("/api/v1/library/scaffold");
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task Scaffold_Operator_200_ReturnsTheEngineTemplateVerbatim()
    {
        // Operator+ so an operator reaching the create page can load the buffer even though the POST
        // below is admin-only.
        HttpResponseMessage r = await Client(_engine, KgsmTier.Operator).GetAsync("/api/v1/library/scaffold");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        Assert.Equal(_engine.Blueprints.Scaffold, (await Json(r)).GetProperty("content").GetString());
    }

    [Fact]
    public async Task Scaffold_EngineReportsNoTemplate_503_NotAFabricatedSkeleton()
    {
        _engine.Blueprints.Scaffold = null;
        try
        {
            HttpResponseMessage r = await Client(_engine, KgsmTier.Operator).GetAsync("/api/v1/library/scaffold");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        }
        finally
        {
            _engine.Blueprints.Reset();
        }
    }

    // ===== create =================================================================================

    [Fact]
    public async Task Create_Operator_403_CreationIsAdminOnly()
    {
        HttpResponseMessage r = await Post(_engine, KgsmTier.Operator, CreateBody("necesse", "name: necesse\n"));
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task Create_Admin_200_WritesToTheUserDirShadowingNothing()
    {
        const string name = "necesse";
        HttpResponseMessage r = await Post(_engine, KgsmTier.Admin, CreateBody(name, "name: necesse\n"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        JsonElement body = await Json(r);
        Assert.Equal("user", body.GetProperty("tier").GetString());
        // A new blueprint shadows nothing — the name was free in both directories.
        Assert.False(body.GetProperty("overridesSystem").GetBoolean());
        Assert.False(body.GetProperty("createdOverride").GetBoolean());
        Assert.Equal("name: necesse\n", _engine.Blueprints.User[name]);
    }

    [Fact]
    public async Task Create_ThreadsTheActorAndOriginIntoTheEngineWrite()
    {
        await Post(_engine, KgsmTier.Admin, CreateBody("necesse", "name: necesse\n", origin: "ui"));

        Assert.Equal("ui", _engine.Blueprints.LastWriteOrigin);
        Assert.False(string.IsNullOrEmpty(_engine.Blueprints.LastWriteActor));
    }

    [Fact]
    public async Task Create_NameOfAUserBlueprint_409_NameTaken()
    {
        HttpResponseMessage r = await Post(_engine, KgsmTier.Admin, CreateBody(UserOnly, "name: whatever\n"));
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("name_taken", (await Json(r)).GetProperty("error").GetProperty("code").GetString());

        // Refused BEFORE the write — the existing blueprint is untouched.
        Assert.Equal("name: teamfortress2\n", _engine.Blueprints.User[UserOnly]);
    }

    [Fact]
    public async Task Create_NameOfAShippedBlueprint_409_NameTaken()
    {
        // A shipped-only name is taken too: creating it here would silently make an override out of what
        // the caller believes is a brand-new game.
        HttpResponseMessage r = await Post(_engine, KgsmTier.Admin, CreateBody(Shipped, "name: factorio\n"));
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("name_taken", (await Json(r)).GetProperty("error").GetProperty("code").GetString());
        Assert.False(_engine.Blueprints.User.ContainsKey(Shipped));
    }

    [Fact]
    public async Task Create_EngineRejectsTheContent_400_WithItsOwnErrorsVerbatim()
    {
        _engine.Blueprints.RejectWith = ["missing required field: runtime", "unknown key: lauch_args"];

        HttpResponseMessage r = await Post(_engine, KgsmTier.Admin, CreateBody("necesse", "junk\n"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);

        JsonElement error = (await Json(r)).GetProperty("error");
        Assert.Equal("blueprint_invalid", error.GetProperty("code").GetString());
        string[] errors = [.. error.GetProperty("details").GetProperty("errors")
            .EnumerateArray().Select(e => e.GetString()!)];
        Assert.Equal(_engine.Blueprints.RejectWith, errors);
        Assert.False(_engine.Blueprints.User.ContainsKey("necesse"));
    }

    [Fact]
    public async Task Create_MissingName_400()
    {
        HttpResponseMessage r = await Post(_engine, KgsmTier.Admin,
            JsonSerializer.Serialize(new { content = "name: x\n" }));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Create_MissingContent_400()
    {
        HttpResponseMessage r = await Post(_engine, KgsmTier.Admin,
            JsonSerializer.Serialize(new { name = "necesse" }));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Create_EngineUnprovisioned_503()
    {
        HttpResponseMessage r = await Post(_noEngine, KgsmTier.Admin, CreateBody("necesse", "name: necesse\n"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
    }

    // ===== helpers ================================================================================

    private async Task<JsonElement> ReadOk(KgsmTier tier, string id)
    {
        HttpResponseMessage r = await Client(_engine, tier).GetAsync($"/api/v1/library/{id}/file");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await Json(r);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    private static string Body(string content) => JsonSerializer.Serialize(new { content });

    private static string CreateBody(string name, string content, string? origin = null) =>
        JsonSerializer.Serialize(new { name, content, origin });

    private static HttpClient Client(AuthTestFactory factory, KgsmTier? tier)
    {
        HttpClient c = factory.CreateClient();
        if (tier is { } t)
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.AccessToken(t));
        return c;
    }

    private static Task<HttpResponseMessage> Post(AuthTestFactory f, KgsmTier? tier, string json) =>
        Client(f, tier).PostAsync("/api/v1/library",
            new StringContent(json, Encoding.UTF8, "application/json"));

    private static Task<HttpResponseMessage> Put(AuthTestFactory f, KgsmTier? tier, string id, string json) =>
        Client(f, tier).PutAsync($"/api/v1/library/{id}/file",
            new StringContent(json, Encoding.UTF8, "application/json"));

    /// <summary><see cref="AuthTestFactory"/> with kgsm-lib's blueprint seam swapped for an in-memory pair
    /// of blueprint directories, so the endpoints exercise their real routing/gating/mapping without a live
    /// kgsm. The fake enforces the two structural rules the real authority does — a write always lands in
    /// the user dir, and a read resolves user-over-system — since those are what the DTO fields report.</summary>
    public sealed class BlueprintTestFactory : AuthTestFactory
    {
        public const string ShippedContent = "# shipped by the engine — do not edit in place\nname: factorio\nruntime: native\n";

        public FakeBlueprintFiles Blueprints { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            // The base factory leaves the engine unprovisioned by design; the blueprint file service is only
            // registered when it IS. A placeholder path is never shelled — both kgsm-lib blueprint services
            // are replaced below.
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Api:KgsmPath"] = "/usr/bin/kgsm",
                    ["Api:KgsmJournalDir"] = Path.Combine(
                        Path.GetTempPath(), $"kgsm-api-tests-bp-{Guid.NewGuid():N}"),
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IBlueprintFiles>();
                services.RemoveAll<IBlueprintService>();
                services.AddSingleton<IBlueprintFiles>(Blueprints);
                services.AddSingleton<IBlueprintService>(Blueprints);
            });
        }
    }

    /// <summary>An in-memory stand-in for BOTH kgsm-lib blueprint services (the file authority and the
    /// path/validation passthrough) — they are one fake because the two answers must agree about which
    /// files exist, which is exactly what the override reporting depends on.</summary>
    public sealed class FakeBlueprintFiles : IBlueprintFiles, IBlueprintService
    {
        public Dictionary<string, string> System { get; } = [];
        public Dictionary<string, string> User { get; } = [];

        /// <summary>When set, the next write is rejected with these as the engine's verdict.</summary>
        public IReadOnlyList<string>? RejectWith { get; set; }

        public string? LastWriteActor { get; private set; }
        public string? LastWriteOrigin { get; private set; }
        public string? LastRemoveActor { get; private set; }
        public string? LastRemoveOrigin { get; private set; }

        public void Reset()
        {
            System.Clear();
            User.Clear();
            System[Shipped] = BlueprintTestFactory.ShippedContent;
            System[Overridden] = "name: palworld\n";
            User[Overridden] = "name: palworld\n# my override\n";
            User[UserOnly] = "name: teamfortress2\n";
            RejectWith = null;
            Scaffold = "# KGSM Blueprint Template\nname: ''\nruntime: native\n";
            LastWriteActor = LastWriteOrigin = LastRemoveActor = LastRemoveOrigin = null;
        }

        // ---- IBlueprintFiles ----

        public FileOpResult<BlueprintFileContent> ReadRaw(string name, long maxBytes)
        {
            // User-over-system, the engine's own precedence.
            bool user = User.TryGetValue(name, out string? content);
            if (!user && !System.TryGetValue(name, out content))
                return FileOpResult<BlueprintFileContent>.Fail(FileOpOutcome.NotFound);

            return FileOpResult<BlueprintFileContent>.Ok(new BlueprintFileContent
            {
                Name = name,
                Content = content!,
                Path = $"/fake/{(user ? "user" : "system")}/{name}.bp.yaml",
                Tier = user ? BlueprintTier.User : BlueprintTier.System,
                HasSystemOriginal = System.ContainsKey(name),
                SizeBytes = Encoding.UTF8.GetByteCount(content!),
                Mtime = DateTimeOffset.UnixEpoch,
                Etag = Etag(content!),
            });
        }

        public FileOpResult<FileStat> WriteRaw(string name, string content, BlueprintWriteOptions opts)
        {
            if (opts.ExpectedEtag is { Length: > 0 } expected)
            {
                FileOpResult<BlueprintFileContent> current = ReadRaw(name, long.MaxValue);
                if (!string.Equals(current.Value?.Etag, expected, StringComparison.Ordinal))
                    return FileOpResult<FileStat>.Fail(FileOpOutcome.EtagMismatch);
            }
            if (RejectWith is { } errors)
                return FileOpResult<FileStat>.Fail(FileOpOutcome.InvalidDraft, errors);

            User[name] = content; // a write ALWAYS lands in the user dir
            LastWriteActor = opts.Actor;
            LastWriteOrigin = opts.Origin;
            return FileOpResult<FileStat>.Ok(new FileStat
            {
                SizeBytes = Encoding.UTF8.GetByteCount(content),
                Mtime = DateTimeOffset.UnixEpoch,
                Etag = Etag(content),
            });
        }

        public FileOpResult Remove(string name, string? actor = null, string? origin = null)
        {
            if (!User.Remove(name)) return FileOpResult.Fail(FileOpOutcome.NotFound);
            LastRemoveActor = actor;
            LastRemoveOrigin = origin;
            return FileOpResult.Ok();
        }

        // ---- IBlueprintService (only the three the editor uses) ----

        /// <summary>The engine's skeleton, or <see langword="null"/> to model an engine that reported no
        /// templates directory.</summary>
        public string? Scaffold { get; set; } = "# KGSM Blueprint Template\nname: ''\nruntime: native\n";

        public string? GetScaffold() => Scaffold;

        public BlueprintCandidates? FindAll(string blueprintName)
        {
            bool user = User.ContainsKey(blueprintName);
            bool system = System.ContainsKey(blueprintName);
            if (!user && !system) return null;

            return new BlueprintCandidates
            {
                Name = blueprintName,
                Resolved = user ? $"/fake/user/{blueprintName}.bp.yaml" : $"/fake/system/{blueprintName}.bp.yaml",
                Candidates =
                [
                    new BlueprintCandidate { Tier = BlueprintTier.User, Path = $"/fake/user/{blueprintName}.bp.yaml", Exists = user },
                    new BlueprintCandidate { Tier = BlueprintTier.System, Path = $"/fake/system/{blueprintName}.bp.yaml", Exists = system },
                ],
            };
        }

        private static string Etag(string content) =>
            "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

        // --- unused by this surface ---
        public FileOpResult<bool> Exists(string name) =>
            FileOpResult<bool>.Ok(User.ContainsKey(name) || System.ContainsKey(name));
        public FileOpResult<FileStat> Create(NativeBlueprintDraft draft, bool overwrite = false, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public string Render(NativeBlueprintDraft draft) => throw new NotImplementedException();
        public FileOpResult<NativeBlueprintDraft> TryParse(string yaml) => throw new NotImplementedException();
        public BlueprintValidation? Validate(string blueprintNameOrPath) => throw new NotImplementedException();
        public Dictionary<string, Blueprint> ListDetailed() => [];
        public List<string> List() => throw new NotImplementedException();
        public List<string> ListDefault() => throw new NotImplementedException();
        public List<string> ListCustom() => throw new NotImplementedException();
        public Blueprint? GetInfo(string blueprintName) => throw new NotImplementedException();
        public string? FindPath(string blueprintName) => throw new NotImplementedException();
    }
}
