using TheKrystalShip.Api.Contracts;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;

namespace TheKrystalShip.Api.Services.Players;

/// <summary>
/// Turns "moderate this roster entry" into the identity token the game's own command expects.
/// </summary>
/// <remarks>
/// <para>
/// <b>The client never names the target.</b> A moderation request carries only the roster's opaque
/// <c>playerIdentity</c>; every identity field used to build the command comes from the server-side
/// <see cref="RosterPlayer"/> record that key resolves to. A request that could supply its own
/// address or name would let any caller ban an arbitrary address the roster never saw — the browser
/// is a client, and a client does not get to choose who gets banned.
/// </para>
/// <para>
/// Which field to send is not this class's opinion either: the blueprint declares it through the
/// placeholder in its command template (<c>kick {ip}</c> asks for an IP), read here via
/// <see cref="ModerationCommand.TryGetTargetKind"/>. So the game decides the kind, the roster
/// supplies the value, and neither the client nor this API invents either half.
/// </para>
/// </remarks>
public static class ModerationTargetResolver
{
    /// <summary>Why a moderation target could not be resolved.</summary>
    public enum Failure
    {
        /// <summary>Resolution succeeded.</summary>
        None,

        /// <summary>The game declares no command for this action, so it is refused rather than
        /// approximated with a different one.</summary>
        Unsupported,

        /// <summary>The game asks for an identity this player record does not carry — e.g. an
        /// account id for a player only ever seen by name. Reported honestly instead of
        /// substituting whichever field happens to be present.</summary>
        NoSuchIdentity
    }

    /// <summary>
    /// Resolve the token to send for one moderation action.
    /// </summary>
    /// <param name="template">The instance's command template for this action.</param>
    /// <param name="player">The roster record naming who is being moderated.</param>
    /// <param name="target">The resolved token, when this returns <see cref="Failure.None"/>.</param>
    /// <param name="kind">The identity kind the template asked for, when known.</param>
    /// <returns><see cref="Failure.None"/> on success, otherwise why it could not be resolved.</returns>
    public static Failure TryResolve(
        string? template, RosterPlayer player, out string target, out ModerationTargetKind kind)
    {
        target = string.Empty;

        if (!ModerationCommand.TryGetTargetKind(template, out kind))
            return Failure.Unsupported;

        string? value = kind switch
        {
            ModerationTargetKind.Ip => AddressOnly(player.PlayerAddr),
            ModerationTargetKind.Name => player.PlayerName,
            ModerationTargetKind.Id => player.PlayerId,
            _ => null
        };

        if (string.IsNullOrWhiteSpace(value))
            return Failure.NoSuchIdentity;

        target = value;
        return Failure.None;
    }

    /// <summary>
    /// What moderation this instance's game supports, read from the templates its blueprint declares.
    /// </summary>
    /// <remarks>
    /// Reported so a client can render only the actions that exist rather than discovering a
    /// <c>409</c> by pressing a button. It never claims support the blueprint did not declare — an
    /// action with no template is <see langword="false"/>, not "probably fine".
    /// </remarks>
    public static ModerationCapability Describe(Instance? instance)
    {
        if (instance is null)
            return new ModerationCapability(false, false, false, null);

        bool kick = ModerationCommand.IsSupported(instance.KickCommand);
        bool ban = ModerationCommand.IsSupported(instance.BanCommand);
        bool unban = ModerationCommand.IsSupported(instance.UnbanCommand);

        // The kind comes off whichever command is declared — a game addresses players one way, and
        // its templates all use the same placeholder. Null when it declares no moderation at all,
        // rather than a default that would imply an identity the game never asked for.
        string? kindName = null;
        foreach (string? template in new[] { instance.KickCommand, instance.BanCommand, instance.UnbanCommand })
        {
            if (ModerationCommand.TryGetTargetKind(template, out ModerationTargetKind k))
            {
                kindName = k.ToString().ToLowerInvariant();
                break;
            }
        }

        return new ModerationCapability(kick, ban, unban, kindName);
    }

    /// <summary>
    /// The address half of a roster <c>PlayerAddr</c>, which is stored as <c>ip:port</c> because that
    /// is what a game's connection log carries. A game that moderates by address means the host, not
    /// the connection — the port is ephemeral and changes on every reconnect, so sending it would
    /// address a socket that no longer exists.
    /// </summary>
    /// <remarks>
    /// IPv6 literals contain colons of their own, so only a trailing <c>:port</c> on a form that is
    /// not bracketed is stripped. A bracketed <c>[::1]:9999</c> loses the port and the brackets;
    /// anything else is handed back untouched rather than truncated at a guess.
    /// </remarks>
    internal static string? AddressOnly(string? addr)
    {
        if (string.IsNullOrWhiteSpace(addr))
            return null;

        addr = addr.Trim();

        // Bracketed IPv6 with a port: [::1]:9999 → ::1
        if (addr.StartsWith('['))
        {
            int close = addr.IndexOf(']');
            return close > 1 ? addr[1..close] : addr;
        }

        int lastColon = addr.LastIndexOf(':');
        if (lastColon <= 0)
            return addr;

        // More than one colon and no brackets means a bare IPv6 literal, which carries no port.
        if (addr.IndexOf(':') != lastColon)
            return addr;

        string port = addr[(lastColon + 1)..];
        return port.Length > 0 && port.All(char.IsAsciiDigit) ? addr[..lastColon] : addr;
    }
}
