using System.Diagnostics;

namespace TheKrystalShip.Api.Services.Leaves;

/// <summary>
/// The seam for restarting a leaf's systemd unit (the apply broker's one privileged operation). A test fake
/// records calls + returns a canned result; the real impl shells <c>systemctl restart</c>.
/// </summary>
public interface IUnitController
{
    /// <summary>Restart <paramref name="unit"/>. Returns <c>true</c> when the restart command itself
    /// succeeded (exit 0); the post-restart health is judged separately by the <see cref="ILeafProbe"/>.</summary>
    Task<bool> RestartAsync(string unit, CancellationToken ct);
}

/// <summary>
/// Restarts a leaf unit via <c>systemctl restart &lt;unit&gt;</c>. The non-interactive privilege comes from a
/// scoped polkit rule + the static drop-in installed <strong>out-of-band</strong> by the deploy/setup
/// (scoped to exactly the four <c>kgsm-*</c> units) — this code only ever shells the restart, it installs
/// nothing. Arguments go through <see cref="ProcessStartInfo.ArgumentList"/> (never a joined string — the
/// ProcessRunner lesson).
/// </summary>
public sealed class SystemctlUnitController(ApiOptions options, ILogger<SystemctlUnitController> logger) : IUnitController
{
    private static readonly TimeSpan RestartTimeout = TimeSpan.FromSeconds(30);

    public async Task<bool> RestartAsync(string unit, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(options.SystemctlPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("restart");
            psi.ArgumentList.Add(unit);

            using var proc = new Process { StartInfo = psi };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RestartTimeout);

            if (!proc.Start())
            {
                logger.LogWarning("systemctl restart {Unit} could not start ({Path})", unit, options.SystemctlPath);
                return false;
            }

            string stderr = await proc.StandardError.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await proc.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            if (proc.ExitCode != 0)
            {
                logger.LogWarning("systemctl restart {Unit} exited {Code}: {Err}", unit, proc.ExitCode, stderr.Trim());
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "systemctl restart {Unit} failed", unit);
            return false;
        }
    }
}
