using TheKrystalShip.KGSM.ComponentSurface;

namespace TheKrystalShip.Api.Services.Leaves;

/// <summary>
/// A leaf's floor: what its own configuration sets, with this API's override layer excluded.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read through the library the leaf serves its own surface with.</b> A leaf that is up answers about
/// its own configuration and this API relays; this path is for the leaf that is DOWN, which is exactly
/// when somebody wants to know what it was configured with. Both answers are about the same file, so
/// they come from the same rules — a second reader here would be a second set, free to disagree about
/// which unit systemd is using or how a boolean is spelled, and to do it only while the leaf is down.
/// </para>
/// <para>
/// <b>This API's own override layer is excluded.</b> That layer is the override provenance tier; folding
/// it into the floor would make every overridden key report as if the leaf had been configured that way
/// by hand. The exclusion is by path — the renderer's own output file — so it holds however the drop-in
/// that loads it is named.
/// </para>
/// <para>
/// <b>Honest about failure.</b> An unreadable source is never treated as an empty one: "the file is not
/// there" and "I could not open it" are different facts, and only the first licenses falling through to
/// the descriptor default. <see cref="ComponentFloor.Complete"/> carries which.
/// </para>
/// </remarks>
public sealed class LeafFloorReader(
    ApiOptions options,
    LeafOverrideRenderer renderer,
    ILogger<ComponentFloorReader> logger)
{
    /// <summary>What a declared source could not tell us, for a leaf with no descriptor to declare any.</summary>
    public static readonly ComponentFloor Unknown =
        new(new Dictionary<string, string>(StringComparer.Ordinal), false);

    public ComponentFloor Read(LeafConfigDescriptor descriptor)
    {
        // Built per leaf rather than held: this API reads many leaves' floors and each one's override
        // file and unit are its own, so there is no single surface's paths to keep.
        var surface = new ComponentSurfaceOptions(
            DescriptorPath: "",                                  // the descriptor is already parsed
            OverridePath: renderer.PathFor(descriptor.Id),
            CommandsPath: null,
            UnitDirectory: options.LeafDropInDir);

        return new ComponentFloorReader(surface, logger).Read(
        [
            // A systemd-unit source names the UNIT and the descriptor's path field carries that name;
            // every other kind names a real file. The reader resolves a unit across the roots systemd
            // reads, so what is passed through is what the descriptor said.
            .. descriptor.FloorSources.Select(s => new ComponentFloorSource(
                s.Kind,
                string.Equals(s.Kind, "systemd-unit", StringComparison.Ordinal) ? descriptor.Unit : s.Path)),
        ]);
    }
}
