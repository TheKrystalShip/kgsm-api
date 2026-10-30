namespace TheKrystalShip.Api.Tests;

/// <summary>A person a test's token names: the token's <c>sub</c> and <c>name</c>.</summary>
/// <param name="Subject">The account, as the provider's tokens carry it.</param>
/// <param name="Name">What the person is called.</param>
public sealed record TestPerson(string Subject, string Name);

/// <summary>The people this suite's tokens name.</summary>
public static class TestIdentity
{
    /// <summary>The standing person every <see cref="AuthTestFactory.AccessToken"/> names.</summary>
    public static readonly TestPerson Person = new("usr_haru", "haru");

    /// <summary>A second person, named by <paramref name="subject"/>, for a test that needs two.</summary>
    public static TestPerson PersonFor(string subject) => new("usr_" + subject, "User " + subject);
}
