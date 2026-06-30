using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.Api;
using TheKrystalShip.Api.Services.Leaves;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Deterministic tests for the <see cref="LeafOverrideRenderer"/> (the leaf-runtime-config file renderer) —
/// the bit that materializes the DB override rows into <c>&lt;leaf&gt;.env</c>: the EnvName=value content,
/// the <c>0600</c> mode, the atomic overwrite, and reset (empty rows ⇒ the file is removed). No factory/DI —
/// it is pure filesystem I/O against a temp dir.
/// </summary>
public sealed class LeafOverrideRendererTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"kgsm-api-ovr-{Guid.NewGuid():N}");
    private readonly LeafOverrideRenderer _renderer;

    public LeafOverrideRendererTests()
    {
        ApiOptions opts = ApiOptions.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["KGSM_API_LEAF_OVERRIDES_DIR"] = _dir })
            .Build());
        _renderer = new LeafOverrideRenderer(opts, NullLogger<LeafOverrideRenderer>.Instance);
    }

    [Fact]
    public void Render_WritesEnvNameValueLines_FromManifest()
    {
        // Rows are keyed by the manifest KEY; the renderer maps each to its real ENV NAME.
        _renderer.Render(ProvisionableLeaf.Monitor,
        [
            new LeafOverrideRow("logLevel", "Debug", false),
            new LeafOverrideRow("intervalMs", "2000", false),
        ]);

        string path = _renderer.PathFor(ProvisionableLeaf.Monitor);
        Assert.True(File.Exists(path));
        string text = File.ReadAllText(path);
        Assert.Contains("Logging__LogLevel__Default=Debug", text);  // mapped via the manifest, not the key
        Assert.Contains("KGSM_MONITOR_INTERVAL_MS=2000", text);
        Assert.DoesNotContain("logLevel=", text);                   // the wire KEY is never written
    }

    [Fact]
    public void Render_FileIs0600()
    {
        _renderer.Render(ProvisionableLeaf.Assistant, [new LeafOverrideRow("webSearchApiKey", "tvly-secret", true)]);
        string path = _renderer.PathFor(ProvisionableLeaf.Assistant);

        if (OperatingSystem.IsLinux())
        {
            UnixFileMode mode = File.GetUnixFileMode(path);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode); // 0600 — group/other have nothing
        }
    }

    [Fact]
    public void Render_Overwrites_NoTempLeftBehind()
    {
        _renderer.Render(ProvisionableLeaf.Monitor, [new LeafOverrideRow("logLevel", "Debug", false)]);
        _renderer.Render(ProvisionableLeaf.Monitor, [new LeafOverrideRow("logLevel", "Warning", false)]);

        string text = File.ReadAllText(_renderer.PathFor(ProvisionableLeaf.Monitor));
        Assert.Contains("Logging__LogLevel__Default=Warning", text);
        Assert.DoesNotContain("Debug", text);
        // No .tmp-* siblings left around (atomic temp+rename cleaned up).
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp-*"));
    }

    [Fact]
    public void Render_EmptyRows_DeletesTheFile()
    {
        _renderer.Render(ProvisionableLeaf.Monitor, [new LeafOverrideRow("logLevel", "Debug", false)]);
        string path = _renderer.PathFor(ProvisionableLeaf.Monitor);
        Assert.True(File.Exists(path));

        _renderer.Render(ProvisionableLeaf.Monitor, []); // reset-to-floor
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Render_StripsNewlinesFromValue_NoInjection()
    {
        // A value can never span lines (it would inject a second KEY=VALUE).
        _renderer.Render(ProvisionableLeaf.Assistant,
            [new LeafOverrideRow("webSearchApiKey", "abc\nLogging__LogLevel__Default=Trace", true)]);
        string text = File.ReadAllText(_renderer.PathFor(ProvisionableLeaf.Assistant));
        string[] kvLines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => !l.StartsWith('#')).ToArray();
        Assert.Single(kvLines); // exactly one override line, not two
        Assert.StartsWith("WebSearch__ApiKey=", kvLines[0]);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
