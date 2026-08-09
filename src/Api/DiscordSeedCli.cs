using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Discord;
using TheKrystalShip.KGSM.Auth.Users;

namespace TheKrystalShip.Api;

/// <summary>
/// <c>kgsm-api user seed-discord</c> — give the Discord identities this host already signs in the
/// KGSM accounts they will need from now on.
/// </summary>
/// <remarks>
/// <para>
/// Authority lives on a KGSM account. Discord identities that have been signing in against a guild
/// role map prove no account yet, so without this each of them arrives as a stranger and waits for an
/// admin — including the people who were operating this host yesterday. This reads the role map one
/// last time and writes what it says onto real accounts, with provenance <c>derived</c> so an access
/// review can see which tiers nobody ever deliberately chose.
/// </para>
/// <para>
/// <b>Who it seeds.</b> The Discord identities in this host's own session registry — the measured set
/// of people who have actually signed in here — plus any named with <c>--user</c>. Not the guild:
/// listing a guild's members needs a privileged intent this application is not granted, and the whole
/// point of the phase is that guild membership stops being what says who belongs. Anyone the seed
/// misses is not locked out, they simply arrive as a new account awaiting approval, which is the
/// correct answer for someone this host has never seen.
/// </para>
/// <para>
/// <b>It writes nothing without <c>--apply</c>.</b> A seed that decides who administers a host should
/// be read before it is true, not after. Re-running is safe: an identity that already proves an
/// account is left exactly as it is, so this never overwrites a tier an admin has since chosen.
/// </para>
/// </remarks>
internal static class DiscordSeedCli
{
    public const string Subcommand = "seed-discord";

    /// <summary>One identity, and what the seed makes of it.</summary>
    private sealed record Candidate(string DiscordId, string? Username, string? Display, KgsmTier Tier, string Note);

    public static async Task<int> RunAsync(string[] args, IUserStore store, ApiOptions options, string sessionsDbPath)
    {
        bool apply = args.Contains("--apply");

        if (string.IsNullOrWhiteSpace(options.DiscordBotToken) || string.IsNullOrWhiteSpace(options.DiscordGuildId))
        {
            await Console.Error.WriteLineAsync(
                "This host has no Discord bot token or guild configured, so there are no roles to read.");
            return 1;
        }

        KgsmRoleMap roleMap = new(options.RoleAdminIds, options.RoleOperatorIds);
        if (roleMap.IsEmpty)
        {
            await Console.Error.WriteLineAsync(
                "No role ids are configured, so every guild member would seed as a viewer. Refusing: " +
                "set KgsmAuth__RoleAdminIds and KgsmAuth__RoleOperatorIds first, or seed nobody.");
            return 1;
        }

        List<string> subjects = [.. Explicit(args)];
        foreach (string fromSessions in ReadSignedInDiscordIds(sessionsDbPath))
        {
            if (!subjects.Contains(fromSessions, StringComparer.Ordinal))
                subjects.Add(fromSessions);
        }

        if (subjects.Count == 0)
        {
            Console.WriteLine("No Discord identity has ever signed in to this host, and none was named. Nothing to seed.");
            return 0;
        }

        Console.WriteLine(apply
            ? $"Seeding {subjects.Count} Discord {(subjects.Count == 1 ? "identity" : "identities")}."
            : $"Reading {subjects.Count} Discord {(subjects.Count == 1 ? "identity" : "identities")}. Nothing is written without --apply.");
        Console.WriteLine();

        using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(15) };
        DiscordDirectory discord = new(
            http,
            new KgsmAuthOptions
            {
                ClientId = options.DiscordClientId,
                ClientSecret = options.DiscordClientSecret,
                BotToken = options.DiscordBotToken,
                GuildId = options.DiscordGuildId,
            },
            // A seed never sends anyone to an authorize URL; the endpoints are required to construct
            // the directory and are unused on this path.
            new DiscordOAuthEndpoints(options.DiscordRedirectUri),
            roleMap);

        IdentityLinkService linking = new(store);
        int seeded = 0, already = 0, skipped = 0, failed = 0;

        Console.WriteLine($"{"DISCORD ID",-22} {"NAME",-22} {"TIER",-10} WHAT HAPPENS");
        foreach (string subject in subjects)
        {
            Candidate? candidate = await DescribeAsync(discord, roleMap, store, subject);
            if (candidate is null)
            {
                failed++;
                Console.WriteLine($"{subject,-22} {"?",-22} {"?",-10} could not be read from Discord — left alone");
                continue;
            }

            if (candidate.Note == "already has an account")
            {
                already++;
                Console.WriteLine($"{subject,-22} {Trim(candidate.Username),-22} {"—",-10} {candidate.Note}");
                continue;
            }

            if (candidate.Tier == KgsmTier.None)
            {
                skipped++;
                Console.WriteLine($"{subject,-22} {Trim(candidate.Username),-22} {"none",-10} {candidate.Note}");
                continue;
            }

            if (!apply)
            {
                seeded++;
                Console.WriteLine($"{subject,-22} {Trim(candidate.Username),-22} {KgsmTiers.ToWire(candidate.Tier),-10} would be created, active");
                continue;
            }

            KgsmIdentity identity = new(
                KgsmActorProvider.Discord, subject,
                candidate.Username ?? subject, candidate.Display ?? candidate.Username ?? subject,
                AvatarUrl: null, Scopes: []);

            LinkResult result = await linking.ProvisionAsync(
                identity, candidate.Tier, TierSource.Derived, UserStatus.Active, DateTimeOffset.UtcNow);

            if (result.Outcome == LinkOutcome.Existing)
            {
                already++;
                Console.WriteLine($"{subject,-22} {Trim(candidate.Username),-22} {"—",-10} already has an account");
                continue;
            }

            seeded++;
            Console.WriteLine(
                $"{subject,-22} {Trim(result.User!.Username),-22} {KgsmTiers.ToWire(candidate.Tier),-10} created, active");
        }

        Console.WriteLine();
        Console.WriteLine(apply
            ? $"Seeded {seeded}. Left alone: {already} with accounts, {skipped} the role map does not elevate, {failed} unreadable."
            : $"Would seed {seeded}. Would leave alone: {already} with accounts, {skipped} the role map does not elevate, {failed} unreadable.");
        if (!apply && seeded > 0)
            Console.WriteLine("Re-run with --apply to write this.");
        // An identity Discord would not answer for is the one outcome worth a non-zero exit: the
        // operator has to decide whether to re-run or seed that person by hand.
        return failed > 0 ? 2 : 0;
    }

    // What the role map makes of one identity, and whether an account already claims it. One lookup:
    // Discord's member object carries both the roles and the user they belong to.
    private static async Task<Candidate?> DescribeAsync(
        DiscordDirectory discord, KgsmRoleMap roleMap, IUserStore store, string subject)
    {
        DiscordMember? member;
        try
        {
            member = await discord.GetGuildMemberAsync(subject, CancellationToken.None);
        }
        catch (KgsmAuthProviderException)
        {
            // Unreadable is not "no roles". Passing an outage off as an empty role list is how a seed
            // silently demotes a room full of operators.
            return null;
        }

        string handle = KgsmActor.Format(KgsmActorProvider.Discord, subject);
        if (await store.FindByCredentialAsync(handle) is not null)
            return new Candidate(subject, member?.Username, member?.Display, KgsmTier.None,
                "already has an account");

        KgsmTier tier = roleMap.Resolve(member?.Roles);
        string note = member is null
            ? "not a member of the guild"
            : tier == KgsmTier.Viewer
                ? "no elevating role — arrives for approval instead"
                : "";

        // The viewer floor is what every guild member gets simply for being in the guild, which is
        // exactly the fact this phase stops treating as authority. Seeding it would write "this person
        // belongs here" on the strength of a chat-server membership.
        return new Candidate(subject, member?.Username, member?.Display,
            tier == KgsmTier.Viewer ? KgsmTier.None : tier, note);
    }

    /// <summary>
    /// The Discord identities this host has minted a session for — the measured set of people who
    /// have actually signed in here.
    /// </summary>
    /// <remarks>
    /// Read straight off the session registry's own file with SQL rather than through EF, because the
    /// CLI runs before any host is built and has no <c>AppDbContext</c>. Every row this reads was
    /// written by <c>SessionStore</c>, and none is modified. A registry that has never been created is
    /// an empty answer, not an error — a host where nobody has signed in has nobody to seed.
    /// </remarks>
    private static IEnumerable<string> ReadSignedInDiscordIds(string sessionsDbPath)
    {
        if (!File.Exists(sessionsDbPath))
            yield break;

        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={sessionsDbPath};Mode=ReadOnly");
        connection.Open();

        using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT UserId FROM sessions WHERE UserId LIKE 'discord:%'";

        using Microsoft.Data.Sqlite.SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string handle = reader.GetString(0);
            if (KgsmActor.TryParse(handle, out _, out string subject) && subject.Length > 0)
                yield return subject;
        }
    }

    private static IEnumerable<string> Explicit(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--user")
                yield return args[i + 1].Trim();
        }
    }

    private static string Trim(string? name) =>
        string.IsNullOrEmpty(name) ? "?" : name.Length <= 22 ? name : name[..22];
}
