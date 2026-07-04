using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Commands;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// <c>CommandGate.Inadmissible</c> — the M3 admissibility gate, extended for the new
/// <see cref="ServerStatus.Starting"/> run-state (item (g) of the tri-state test matrix). Decisions
/// locked here: <c>start</c> against a starting server is the same no-op class as start-when-running
/// (409); <c>stop</c> against a starting server IS admissible (an operator must be able to abort a
/// server stuck mid-boot); <c>update</c> is blocked against starting for the same "files in use" reason
/// it is blocked against running.
/// </summary>
public sealed class CommandGateTests
{
    [Fact]
    public void Start_WhenStarting_IsInadmissible()
    {
        string? reason = CommandGate.Inadmissible(CommandVerb.Start, ServerStatus.Starting);

        Assert.NotNull(reason);
        Assert.Contains("starting", reason);
    }

    [Fact]
    public void Start_WhenRunning_IsInadmissible()
    {
        Assert.NotNull(CommandGate.Inadmissible(CommandVerb.Start, ServerStatus.Running));
    }

    [Fact]
    public void Start_WhenStopped_IsAdmissible()
    {
        Assert.Null(CommandGate.Inadmissible(CommandVerb.Start, ServerStatus.Stopped));
    }

    [Fact]
    public void Stop_WhenStarting_IsAdmissible()
    {
        // An operator must be able to abort a server stuck mid-boot — stop is NOT gated on `starting`.
        Assert.Null(CommandGate.Inadmissible(CommandVerb.Stop, ServerStatus.Starting));
    }

    [Fact]
    public void Stop_WhenStopped_IsInadmissible()
    {
        Assert.NotNull(CommandGate.Inadmissible(CommandVerb.Stop, ServerStatus.Stopped));
    }

    [Fact]
    public void Stop_WhenRunning_IsAdmissible()
    {
        Assert.Null(CommandGate.Inadmissible(CommandVerb.Stop, ServerStatus.Running));
    }

    [Fact]
    public void Update_WhenStarting_IsInadmissible_SameFilesInUseReasonAsRunning()
    {
        string? reason = CommandGate.Inadmissible(CommandVerb.Update, ServerStatus.Starting);

        Assert.NotNull(reason);
        Assert.Equal(CommandGate.Inadmissible(CommandVerb.Update, ServerStatus.Running), reason);
    }

    [Fact]
    public void Update_WhenStopped_IsAdmissible()
    {
        Assert.Null(CommandGate.Inadmissible(CommandVerb.Update, ServerStatus.Stopped));
    }

    [Theory]
    [InlineData(CommandVerb.Start)]
    [InlineData(CommandVerb.Stop)]
    [InlineData(CommandVerb.Update)]
    public void UnknownStatus_NeverBlocks_AnyVerb(string verb)
    {
        // We cannot honestly call a transition a no-op when we could not read the current state.
        Assert.Null(CommandGate.Inadmissible(verb, ServerStatus.Unknown));
    }

    [Fact]
    public void Restart_AgainstStarting_IsAdmissible_NoSpecialCaseYet()
    {
        // Restart has no explicit gate rule (falls through to the engine as the backstop) — unchanged by
        // this increment; asserted so a future change to this is a deliberate, reviewed decision.
        Assert.Null(CommandGate.Inadmissible(CommandVerb.Restart, ServerStatus.Starting));
    }

    [Fact]
    public void OpenPorts_AgainstStarting_IsAdmissible_AlwaysDeclarative()
    {
        Assert.Null(CommandGate.Inadmissible(CommandVerb.OpenPorts, ServerStatus.Starting));
    }
}
