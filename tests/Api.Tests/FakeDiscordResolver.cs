using TheKrystalShip.KGSM.Auth;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// The people this suite's sessions name — Discord-shaped, the way the auth anchor names somebody who
/// signed in through Discord.
/// </summary>
/// <remarks>
/// A session carries who someone is and nothing about what they may do: the tier every gate reads is
/// the one on the account the identity proves in this node's replica. So a test chooses a person here
/// and sets their account separately (<see cref="AuthTestFactory.SetAccount"/>), which is the same
/// split production has.
/// </remarks>
public static class FakeDiscordResolver
{
    /// <summary>The standing identity every <see cref="AuthTestFactory.AccessToken"/> names.</summary>
    public static readonly KgsmIdentity Identity =
        new(KgsmActorProvider.Discord, "198772043", "haru", "haru",
            "https://cdn.discordapp.com/avatars/198772043/abc.png", ["identify", "guilds"]);

    /// <summary>A second person, named by <paramref name="subject"/>, for a test that needs two.</summary>
    public static KgsmIdentity IdentityFor(string subject) =>
        Identity with { Subject = subject, Username = "user" + subject, Display = "User " + subject };
}
