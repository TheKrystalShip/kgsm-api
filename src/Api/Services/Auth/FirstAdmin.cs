using System.Security.Cryptography;
using System.Text;

using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Users;

namespace TheKrystalShip.Api.Services.Auth;

/// <summary>
/// The administrator a host begins with, and the file its password is left in.
/// </summary>
/// <remarks>
/// <para>
/// A host with no accounts has nobody who can sign in to make one, so the first one has to come from
/// somewhere that is not a browser. It is created on the first start that finds the account store
/// empty, and its password — generated, never chosen — is written to
/// <see cref="ApiOptions.InitialAdminPasswordPath"/> for whoever has a shell on the host to read.
/// <c>kgsm-api user bootstrap</c> does the same thing from a terminal and prints the password instead.
/// </para>
/// <para>
/// <b>The file is written once and never rewritten.</b> It is removed the first time the account it
/// names signs in with a password, which is the moment its contents stop being the only copy of
/// anything; an admin may also just delete it. A host where the first sign-in comes through an
/// identity provider instead keeps it until somebody does.
/// </para>
/// </remarks>
internal static class FirstAdmin
{
    /// <summary>The name the account is created under when nobody chooses one.</summary>
    public const string DefaultUsername = "admin";

    private const string UsernameField = "username:";
    private const string PasswordField = "password:";

    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>
    /// Create the first administrator, if and only if this host has no accounts at all.
    /// </summary>
    /// <returns>
    /// The generated password, or <see langword="null"/> when an account already exists and nothing
    /// was done.
    /// </returns>
    /// <remarks>
    /// Deliberately a no-op on a populated store, so every caller can run it unconditionally on every
    /// start and it fires exactly once.
    /// </remarks>
    public static async Task<string?> CreateAsync(
        IUserStore store, LocalSignInService signIn, string username, CancellationToken ct = default)
    {
        if ((await store.ListAsync(ct)).Count > 0)
            return null;

        string password = GeneratePassword();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        KgsmUser user = new(
            UserIds.NewUserId(), username, "Administrator", KgsmTier.Admin,
            // The host itself decided this, at the only moment it could — as deliberate as a grant gets,
            // and the provenance that spares the account from the pending-account expiry.
            TierSource.Granted, UserStatus.Active, now, now);

        await store.CreateAsync(user, ct);
        await signIn.SetPasswordAsync(user.UserId, password, now, ct);
        return password;
    }

    /// <summary>
    /// Leave the credentials where a person with a shell on this host will find them, owner-readable
    /// only. Reports whether the file was written.
    /// </summary>
    public static bool WritePasswordFile(string path, string username, string password, ILogger logger)
    {
        try
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } dir)
                Directory.CreateDirectory(dir);

            using FileStream file = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
            // Narrowed through the handle before the password is written — a create-then-chmod leaves
            // it readable for as long as the two calls are apart.
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(file.SafeFileHandle, OwnerOnly);
            file.Write(Encoding.UTF8.GetBytes(
                $"""
                # The KGSM administrator this host was created with.
                # Sign in, change the password, then delete this file. Nothing rewrites it.
                {UsernameField} {username}
                {PasswordField} {password}

                """));
            return true;
        }
        catch (Exception e)
        {
            // The account exists either way, and it is the account that matters. Say the password out
            // loud in the log rather than leaving a host with an administrator nobody can be.
            logger.LogWarning(e,
                "The first administrator's password could not be written to {Path}. It is '{Password}' "
                + "for the account '{Username}', and is not recoverable once this line is gone.",
                path, password, username);
            return false;
        }
    }

    /// <summary>
    /// Remove the password file once <paramref name="username"/> has signed in with a password — the
    /// point at which what it holds is no longer the only way into this host.
    /// </summary>
    /// <remarks>
    /// Scoped to the account the file names, because a host can grow other accounts from the shell
    /// before anyone signs in, and a viewer's first login is not what this file is waiting for.
    /// Absent, unreadable and naming somebody else are one outcome: nothing happens.
    /// </remarks>
    public static void ConsumePasswordFile(string path, string username, ILogger logger)
    {
        try
        {
            if (!File.Exists(path) || !string.Equals(ReadUsername(path), username, StringComparison.Ordinal))
                return;

            File.Delete(path);
            logger.LogInformation(
                "'{Username}' has signed in, so the initial administrator password at {Path} is gone.",
                username, path);
        }
        catch (Exception e)
        {
            // A sign-in that worked is not a failed request because a file could not be tidied away.
            logger.LogWarning(e, "The initial administrator password at {Path} could not be removed.", path);
        }
    }

    /// <summary>The account the password file names, or <see langword="null"/> when it names none.</summary>
    private static string? ReadUsername(string path)
    {
        foreach (string line in File.ReadLines(path))
        {
            if (line.StartsWith(UsernameField, StringComparison.Ordinal))
                return line[UsernameField.Length..].Trim();
        }

        return null;
    }

    /// <summary>
    /// A password nobody has to invent: 160 bits, base32-ish over an unambiguous alphabet.
    /// </summary>
    /// <remarks>
    /// The alphabet drops <c>0/O</c> and <c>1/l/I</c> because this is read off a terminal and typed
    /// into a browser, and a password somebody mistypes twice is a password they replace with a worse
    /// one. Even at 32 characters that leaves far more entropy than anything an attacker guesses.
    /// </remarks>
    public static string GeneratePassword()
    {
        const string alphabet = "abcdefghijkmnpqrstuvwxyz23456789";
        char[] chars = new char[32];
        for (int i = 0; i < chars.Length; i++)
            chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];

        // Grouped, because it is going to be read off a screen.
        return $"{new string(chars, 0, 8)}-{new string(chars, 8, 8)}-" +
               $"{new string(chars, 16, 8)}-{new string(chars, 24, 8)}";
    }
}
