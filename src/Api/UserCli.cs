using System.Security.Cryptography;

using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Users;

namespace TheKrystalShip.Api;

/// <summary>
/// <c>kgsm-api user …</c> — managing accounts from the shell.
/// </summary>
/// <remarks>
/// <para>
/// This exists for the two moments the panel cannot help. The first is the beginning: a host with no
/// accounts has nobody who can sign in to create one, and with no identity provider configured there
/// is no other door either. The second is being locked out — a forgotten password, a last admin
/// disabled by accident — where the only remaining authority is having a shell on the host, which is
/// the correct authority for it.
/// </para>
/// <para>
/// It writes the same file the running service reads, which is safe because both go through
/// <see cref="SqliteUserStore"/> and the store is built for two processes. The service does not need
/// restarting for a change here to take effect.
/// </para>
/// </remarks>
internal static class UserCli
{
    public const string Verb = "user";

    /// <summary>Run a <c>user</c> subcommand. Returns the process exit code.</summary>
    public static async Task<int> RunAsync(string[] args, string storePath)
    {
        string? subcommand = args.Length > 1 ? args[1] : null;

        SqliteUserStore store;
        try
        {
            store = new SqliteUserStore(new UserStoreOptions { Path = storePath });
        }
        catch (Exception e)
        {
            await Console.Error.WriteLineAsync($"Could not open the account store at {storePath}: {e.Message}");
            // The overwhelmingly likely cause on a host that has not been provisioned yet, and the
            // one thing that turns this error into a fix.
            await Console.Error.WriteLineAsync("If this host has not been set up, run deploy/setup.sh first.");
            return 1;
        }

        LocalSignInService signIn = new(store, new IdentityPasswordHasher(), new UserStoreAuthority(store));

        return subcommand switch
        {
            "bootstrap" => await BootstrapAsync(store, signIn, args),
            "create" => await CreateAsync(store, signIn, args),
            "passwd" => await PasswdAsync(store, signIn, args),
            "list" => await ListAsync(store),
            _ => Usage(subcommand),
        };
    }

    /// <summary>
    /// <c>bootstrap</c> — create the first admin, but only on a host that has no accounts at all.
    /// </summary>
    /// <remarks>
    /// Deliberately a no-op when any account exists, so a deploy script can call it every time and it
    /// fires exactly once. The password is generated here and printed once: a password chosen by a
    /// script is a password that ends up in the script, and one printed to a terminal is one somebody
    /// has to actually copy.
    /// </remarks>
    private static async Task<int> BootstrapAsync(IUserStore store, LocalSignInService signIn, string[] args)
    {
        if ((await store.ListAsync()).Count > 0)
        {
            Console.WriteLine("This host already has KGSM accounts; leaving them alone.");
            return 0;
        }

        string username = Option(args, "--username") ?? "admin";
        if (!Usernames.IsValid(username))
            return Fail($"'{username}' is not a usable username.");

        string password = GeneratePassword();
        await CreateUserAsync(store, signIn, username, "Administrator", KgsmTier.Admin, password);

        Console.WriteLine();
        Console.WriteLine("  Created the first KGSM administrator.");
        Console.WriteLine();
        Console.WriteLine($"    username:  {username}");
        Console.WriteLine($"    password:  {password}");
        Console.WriteLine();
        Console.WriteLine("  This password is shown once and is not stored anywhere in readable form.");
        Console.WriteLine("  Sign in to the Control Panel and change it.");
        Console.WriteLine();
        return 0;
    }

    /// <summary><c>create</c> — add an account. Generates a password when none is given.</summary>
    private static async Task<int> CreateAsync(IUserStore store, LocalSignInService signIn, string[] args)
    {
        string? username = Option(args, "--username");
        if (username is null || !Usernames.IsValid(username))
            return Fail("--username is required, and must be a usable username.");

        if (await store.FindByUsernameAsync(username) is not null)
            return Fail($"'{username}' is already taken on this host.");

        string tierName = Option(args, "--tier") ?? KgsmTiers.Viewer;
        KgsmTier tier = KgsmTiers.Parse(tierName);
        if (tier == KgsmTier.None && tierName != KgsmTiers.None)
            return Fail($"'{tierName}' is not a tier.");

        string? password = Option(args, "--password");
        bool generated = password is null;
        password ??= GeneratePassword();

        await CreateUserAsync(store, signIn, username,
            Option(args, "--display") ?? username, tier, password);

        Console.WriteLine($"Created '{username}' ({KgsmTiers.ToWire(tier)}).");
        if (generated)
            Console.WriteLine($"Password: {password}");

        return 0;
    }

    /// <summary><c>passwd</c> — set an account's password. The way back in after a lockout.</summary>
    private static async Task<int> PasswdAsync(IUserStore store, LocalSignInService signIn, string[] args)
    {
        string? username = Option(args, "--username");
        if (username is null)
            return Fail("--username is required.");

        KgsmUser? user = await store.FindByUsernameAsync(username);
        if (user is null)
            return Fail($"No account '{username}' on this host.");

        string? password = Option(args, "--password");
        bool generated = password is null;
        password ??= GeneratePassword();

        await signIn.SetPasswordAsync(user.UserId, password, DateTimeOffset.UtcNow);

        Console.WriteLine($"Set the password on '{user.Username}'.");
        if (generated)
            Console.WriteLine($"Password: {password}");

        return 0;
    }

    /// <summary><c>list</c> — who exists, and what they may do.</summary>
    private static async Task<int> ListAsync(IUserStore store)
    {
        IReadOnlyList<KgsmUser> all = await store.ListAsync();
        if (all.Count == 0)
        {
            Console.WriteLine("No KGSM accounts on this host. Run 'kgsm-api user bootstrap' to create the first.");
            return 0;
        }

        Console.WriteLine($"{"USERNAME",-24} {"TIER",-10} {"STATUS",-10} ID");
        foreach (KgsmUser user in all)
        {
            Console.WriteLine(
                $"{user.Username,-24} {KgsmTiers.ToWire(user.Tier),-10} " +
                $"{UserStatuses.ToWire(user.Status),-10} {user.UserId}");
        }

        return 0;
    }

    private static async Task CreateUserAsync(
        IUserStore store, LocalSignInService signIn,
        string username, string display, KgsmTier tier, string password)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        KgsmUser user = new(
            UserIds.NewUserId(), username, display, tier,
            // Chosen by a person at a shell, which is as deliberate as a grant gets.
            TierSource.Granted, UserStatus.Active, now, now);

        await store.CreateAsync(user);
        await signIn.SetPasswordAsync(user.UserId, password, now);
    }

    /// <summary>
    /// A password nobody has to invent: 160 bits, base32-ish over an unambiguous alphabet.
    /// </summary>
    /// <remarks>
    /// The alphabet drops <c>0/O</c> and <c>1/l/I</c> because this is read off a terminal and typed
    /// into a browser, and a password somebody mistypes twice is a password they replace with a worse
    /// one. Even at 32 characters that leaves far more entropy than anything an attacker guesses.
    /// </remarks>
    private static string GeneratePassword()
    {
        const string alphabet = "abcdefghijkmnpqrstuvwxyz23456789";
        char[] chars = new char[32];
        for (int i = 0; i < chars.Length; i++)
            chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];

        // Grouped, because it is going to be read off a screen.
        return $"{new string(chars, 0, 8)}-{new string(chars, 8, 8)}-" +
               $"{new string(chars, 16, 8)}-{new string(chars, 24, 8)}";
    }

    /// <summary>The value of <paramref name="name"/>, as <c>--name value</c> or <c>--name=value</c>.</summary>
    private static string? Option(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == name && i + 1 < args.Length)
                return args[i + 1];
            if (args[i].StartsWith(name + "=", StringComparison.Ordinal))
                return args[i][(name.Length + 1)..];
        }

        return null;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    private static int Usage(string? subcommand)
    {
        if (subcommand is not null)
            Console.Error.WriteLine($"Unknown subcommand '{subcommand}'.");

        Console.Error.WriteLine("""
            Usage: kgsm-api user <command>

              bootstrap [--username admin]      Create the first administrator, if this host has no
                                                accounts at all. Prints a generated password once.
              create --username <name> [--tier viewer|operator|admin]
                     [--display <name>] [--password <password>]
              passwd --username <name> [--password <password>]
              list                              Every account, with its tier and status.

            A generated password is printed once and stored only as a hash.
            """);

        return subcommand is null ? 0 : 1;
    }
}
