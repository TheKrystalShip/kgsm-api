using TheKrystalShip.Api.Contracts;
using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.Api.Services.Audit;

/// <summary>
/// What an audit row looks like to a reader below operator: everything that happened, without the
/// values that identify a person or carry operator-level content.
/// </summary>
/// <remarks>
/// <para>
/// <b>The row itself is never withheld.</b> A viewer sees that somebody was banned, that a console
/// command was run, that a player joined — the trail is the same length whoever reads it, and only the
/// values inside it differ. Hiding the row would make the same feed report a different history to two
/// people, which is the one thing an audit surface must not do.
/// </para>
/// <para>
/// <b>Which values those are is <see cref="KgsmEventCatalog"/>'s answer, not this file's.</b> The
/// engine says what each payload field holds — a network address identifies a person, a console
/// command may carry a credential, a moderation target is whichever of the two the game's blueprint
/// declares — and this turns that into the one policy decision the Control Panel makes: anything not
/// public is operator and above. The catalog states facts; the tier is this surface's to choose, and
/// choosing differently from the Discord bot (which shows none of them to anybody) is exactly what
/// separating the two allows.
/// </para>
/// <para>
/// A field's classification is the same on every event that carries it — kgsm-lib has a test to that
/// effect — so a lookup by name is sound, and it is what lets this work on a shaped record that no
/// longer remembers which engine event produced it.
/// </para>
/// </remarks>
public static class AuditRedaction
{
    /// <summary>
    /// The meta keys that need operator, taken off the engine's own classification. The API writes
    /// them camel-cased where kgsm names them Pascal-cased, so the comparison ignores case.
    /// </summary>
    private static readonly HashSet<string> Restricted = new(
        KgsmEventCatalog.All
            .SelectMany(d => d.Fields)
            .Where(f => f.Sensitivity != FieldSensitivity.Public)
            .Select(f => f.Name),
        StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="metaKey"/> is one a reader below operator does not get.</summary>
    public static bool IsRestricted(string metaKey) => Restricted.Contains(metaKey);

    /// <summary>
    /// <paramref name="records"/> as a reader below operator sees them. Rows with nothing restricted
    /// on them come back untouched, which is nearly all of them.
    /// </summary>
    public static IReadOnlyList<AuditRecord> ForViewer(IReadOnlyList<AuditRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        return [.. records.Select(ForViewer)];
    }

    /// <summary>One record as a reader below operator sees it.</summary>
    public static AuditRecord ForViewer(AuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (record.Meta is not { Count: > 0 } meta || !meta.Keys.Any(IsRestricted))
            return record;

        Dictionary<string, string> kept = meta
            .Where(kv => !IsRestricted(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        return record with
        {
            Meta = kept.Count == 0 ? null : kept,
            Summary = Summary(record),
        };
    }

    /// <summary>
    /// The sentence for a reader who does not get the value it names.
    /// </summary>
    /// <remarks>
    /// Two actions put a restricted value in their own summary rather than only in <c>meta</c>, so
    /// stripping the meta alone would leave it printed in the line above. Both are rebuilt by
    /// <see cref="AuditMapping"/>'s own summary builders — the same call the mapper makes for an event
    /// that carried no such value — so a viewer reads a real sentence this surface already produces
    /// rather than a second wording of one. Every other action's summary is composed of public values
    /// and is passed through untouched.
    /// </remarks>
    private static string Summary(AuditRecord record) => record.Action switch
    {
        var a when a == KgsmEventCatalog.NameOf<InstanceInputSentData>() =>
            AuditMapping.ConsoleInputSummary(null, record.ServerId ?? ""),

        var a when a == KgsmEventCatalog.NameOf<InstancePlayerKickedData>() =>
            AuditMapping.ModerationSummary("kicked", null, record.ServerId ?? ""),
        var a when a == KgsmEventCatalog.NameOf<InstancePlayerBannedData>() =>
            AuditMapping.ModerationSummary("banned", null, record.ServerId ?? ""),
        var a when a == KgsmEventCatalog.NameOf<InstancePlayerUnbannedData>() =>
            AuditMapping.ModerationSummary("unbanned", null, record.ServerId ?? ""),

        _ => record.Summary,
    };
}
