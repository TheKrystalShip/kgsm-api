using TheKrystalShip.KGSM.Auth.Sessions;

namespace TheKrystalShip.Api.Services.Auth;

/// <summary>
/// The Control Panel this node serves, as a client of the cluster's sign-in provider.
/// </summary>
/// <remarks>
/// <para>
/// Announced as paths, never URLs: the provider joins them to the browser address the cluster's roster
/// hands out for this node, so the panel a person opens here is registered with no operator step and
/// this node can name no origin but its own.
/// </para>
/// <para>
/// The paths are served by the panel's own fallback — a navigation that matches no route returns the
/// application, which reads where it landed.
/// </para>
/// </remarks>
public static class PanelClient
{
    /// <summary>Where the provider sends a browser back with a code.</summary>
    public const string RedirectPath = "/signed-in";

    /// <summary>Where the provider sends a browser back once it has signed out.</summary>
    public const string PostLogoutRedirectPath = "/";

    /// <summary>What a person is shown on the sign-in page: whose sign-in they are completing.</summary>
    public const string Name = "Control Panel";

    /// <summary>The published fact.</summary>
    public static ClusterClientAnnouncement Announcement { get; } =
        new(Name, [RedirectPath], [PostLogoutRedirectPath]);
}
