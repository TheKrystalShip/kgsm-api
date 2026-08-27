namespace TheKrystalShip.Api.Services.Audit;

/// <summary>
/// What a stored audit row's action is called now.
/// </summary>
/// <remarks>
/// <para>
/// The local table holds rows from before this API stopped keeping its own copy of what the engine
/// did, and each names its fact in the vocabulary of the build that wrote it. A row is a record and
/// is never rewritten, so the resolution happens on the way out: a reader asks one question in one
/// vocabulary and reaches every row that answers it, rather than having to know which spelling was
/// current on the day.
/// </para>
/// <para>
/// <b>Forward only.</b> It answers "what is this called now", never the reverse. Two spellings
/// resolving to one name is normal and correct — a revocation was once three actions and is one
/// event whose scope says how far it reached.
/// </para>
/// <para>
/// ⚠ <b>Unlike a journal's, these rows never age out</b>, so this table has no end date: it lives as
/// long as the oldest row does. Anything not listed is already current and passes through.
/// </para>
/// </remarks>
public static class StoredActionNames
{
    private static readonly Dictionary<string, string> Current = new(StringComparer.Ordinal)
    {
        ["server.start"] = "server.started",
        ["server.stop"] = "server.stopped",
        ["server.restart"] = "server.restarted",
        ["server.update"] = "server.updated",
        ["server.update_available"] = "server.update.available",
        ["server.install"] = "server.installed",
        ["server.uninstall"] = "server.uninstalled",
        ["server.move"] = "server.moved",
        ["server.rename"] = "server.renamed",
        ["server.crash"] = "server.crashed",

        ["backup.create"] = "backup.created",
        ["backup.restore"] = "backup.restored",
        ["backup.delete"] = "backup.deleted",
        ["backup.prune"] = "backup.pruned",
        ["backup.pin"] = "backup.pinned",
        ["backup.unpin"] = "backup.unpinned",
        ["backup.download"] = "backup.downloaded",

        ["network.ports.open"] = "network.ports.opened",
        ["network.ports.close"] = "network.ports.closed",
        ["network.upnp.open"] = "network.upnp.opened",
        ["network.upnp.close"] = "network.upnp.closed",
        ["network.upnp.reassert"] = "network.upnp.reasserted",

        ["player.join"] = "player.joined",
        ["player.leave"] = "player.left",
        ["player.kick"] = "player.kicked",
        ["player.ban"] = "player.banned",
        ["player.unban"] = "player.unbanned",

        ["config.set"] = "config.changed",
        ["console.input"] = "console.input.sent",
        ["file.write"] = "file.written",

        ["blueprint.write"] = "blueprint.updated",
        ["blueprint.revert"] = "blueprint.removed",

        ["library.add"] = "library.added",
        ["library.remove"] = "library.removed",
        ["library.rename"] = "library.renamed",

        ["auth.login"] = "auth.signed_in",
        ["auth.logout"] = "auth.signed_out",
        ["auth.cluster_session"] = "auth.cluster.vouched",
        ["auth.session.revoke"] = "auth.session.revoked",
        ["auth.session.revoke.all"] = "auth.session.revoked",
        ["auth.session.revoke.admin"] = "auth.session.revoked",

        ["user.provision"] = "user.provisioned",
        ["user.approve"] = "user.approved",
        ["user.disable"] = "user.disabled",
        ["user.tier_change"] = "user.tier_changed",
        ["user.delete"] = "user.deleted",
        ["user.password"] = "user.password_changed",

        ["identity.link"] = "identity.linked",
        ["identity.unlink"] = "identity.unlinked",

        ["service.connect"] = "service.connected",
        ["service.disconnect"] = "service.disconnected",
        ["service.config"] = "service.config_changed",
        ["service.restart"] = "service.restarted",

        ["host.threshold.breach"] = "host.threshold.breached",
        ["host.threshold.clear"] = "host.threshold.cleared",
    };

    /// <summary>The current name for <paramref name="stored"/>, or <paramref name="stored"/> itself.</summary>
    public static string Canonical(string stored) =>
        Current.TryGetValue(stored, out string? now) ? now : stored;
}
