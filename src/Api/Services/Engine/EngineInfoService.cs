using System.Text.Json;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Api.Services.Engine;

/// <summary>
/// The engine's identity probe — asks <c>kgsm</c> its version and directory layout through kgsm-lib and
/// caches the answer. Two surfaces read it: the Services board (the engine's pseudo-leaf row derives its
/// availability from whether the probe answered) and <c>GET /hosts/{id}/engine</c> (the pseudo-leaf's
/// Overview).
/// </summary>
/// <remarks>
/// <para>
/// The probe IS the availability measurement: a <c>--version</c> that answers proves the engine is
/// invocable, and one that doesn't is the honest "unreachable" — nothing here infers engine state from
/// any other signal. Success is cached for <see cref="SuccessTtl"/> (the version changes only on an
/// engine deploy), failure for <see cref="FailureTtl"/> so a broken engine is re-checked promptly without
/// spawning a process per request.
/// </para>
/// <para>
/// kgsm-lib's calls are synchronous process invocations, so they run off the request thread —
/// same as every other engine call site.
/// </para>
/// </remarks>
public sealed class EngineInfoService(
    IServiceScopeFactory scopeFactory,
    ApiOptions options,
    ILogger<EngineInfoService> logger)
{
    private static readonly TimeSpan SuccessTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan FailureTtl = TimeSpan.FromSeconds(15);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private EngineInfo? _cached;
    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;
    private bool _cachedOk;

    /// <summary>
    /// The engine's identity, or null when the engine is not configured on this host or would not
    /// answer. Served from cache within the TTL; one caller probes at a time and the rest wait for
    /// its answer rather than each spawning their own <c>kgsm</c>.
    /// </summary>
    public async Task<EngineInfo?> GetAsync(CancellationToken ct)
    {
        if (!options.KgsmProvisioned)
            return null;

        if (IsFresh())
            return _cached;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsFresh())
                return _cached;

            EngineInfo? info = await Task.Run(Probe, ct).ConfigureAwait(false);
            _cached = info;
            _cachedOk = info is not null;
            _cachedAt = DateTimeOffset.UtcNow;
            return info;
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool IsFresh()
    {
        TimeSpan age = DateTimeOffset.UtcNow - _cachedAt;
        return age < (_cachedOk ? SuccessTtl : FailureTtl);
    }

    private EngineInfo? Probe()
    {
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            if (scope.ServiceProvider.GetService<IKgsmClient>() is not IKgsmClient kgsm)
                return null;

            KgsmResult version = kgsm.GetVersion();
            string? parsed = ParseVersion(version);
            if (parsed is null)
            {
                logger.LogWarning("engine probe: kgsm --version failed (exit={ExitCode})", version.ExitCode);
                return null;
            }

            // The layout is optional detail on top of an already-proven engine: an engine that answered
            // its version but not its paths still gets an identity, with Paths honestly null.
            return new EngineInfo(parsed, options.KgsmPath, ParsePaths(kgsm.AdHoc("--paths", "--json")));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "engine probe: kgsm did not answer");
            return null;
        }
    }

    /// <summary>
    /// The version token out of <c>kgsm --version</c>'s banner ("KGSM, version 3.18.0-rc4" + license
    /// lines) — the last whitespace-separated token of the first non-empty line. Null when the command
    /// failed or the banner is empty.
    /// </summary>
    internal static string? ParseVersion(KgsmResult result)
    {
        if (result.IsFailure)
            return null;

        string? firstLine = result.Stdout
            .Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0);
        string? token = firstLine?.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return string.IsNullOrEmpty(token) ? null : token;
    }

    /// <summary>
    /// The selected layout keys out of <c>kgsm --paths --json</c> (<c>{system:{...},user:{...}}</c>).
    /// Null when the command failed or its output isn't the expected JSON; a missing key is a null field.
    /// </summary>
    internal static EnginePaths? ParsePaths(KgsmResult result)
    {
        if (result.IsFailure)
            return null;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(result.Stdout);
            string? Key(string section, string key) =>
                doc.RootElement.TryGetProperty(section, out JsonElement s)
                && s.ValueKind == JsonValueKind.Object
                && s.TryGetProperty(key, out JsonElement v)
                && v.ValueKind == JsonValueKind.String
                    ? v.GetString()
                    : null;

            return new EnginePaths(
                Root: Key("system", "KGSM_ROOT"),
                ConfigFile: Key("user", "KGSM_CONFIG_FILE"),
                InstancesDir: Key("user", "KGSM_INSTANCES_DIR"),
                BlueprintsDir: Key("system", "KGSM_SYSTEM_BLUEPRINTS_DIR"));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
