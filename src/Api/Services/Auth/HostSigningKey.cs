using System.Security.Cryptography;
using System.Text;

namespace TheKrystalShip.Api.Services.Auth;

/// <summary>
/// The secret this host signs its sessions with, resolved once per process.
/// </summary>
/// <remarks>
/// <para>
/// A configured <c>Api__SigningKey</c> is the answer whenever there is one, and no file is read or
/// written. With none, the key is 384 random bits kept in <see cref="ApiOptions.SigningKeyPath"/> and
/// reused on every later start, so a host nobody handed a secret to still keeps people signed in
/// across a restart and an upgrade. That matters more than it sounds: the refresh window is 30 days,
/// and a key that changes per process turns every deploy into a fleet-wide sign-out.
/// </para>
/// <para>
/// The file holds the secret and nothing else, at <c>0600</c>. A key that cannot be written is still
/// generated and the service still starts — an API that refuses to run is worse than one whose
/// sessions do not outlive it — and the warning says which of the two happened.
/// </para>
/// </remarks>
public sealed class HostSigningKey
{
    /// <summary>Matches <c>openssl rand -base64 48</c>, which is what an operator setting one is told to run.</summary>
    private const int KeyBytes = 48;

    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>The secret, however it was arrived at.</summary>
    public string Value { get; }

    /// <summary>
    /// The file the secret is kept in, or <see langword="null"/> when configuration supplied one and
    /// nothing on disk is involved.
    /// </summary>
    public string? FilePath { get; }

    public HostSigningKey(ApiOptions options, ILogger<HostSigningKey> logger)
    {
        if (!string.IsNullOrWhiteSpace(options.SigningKey))
        {
            Value = options.SigningKey;
            return;
        }

        FilePath = options.SigningKeyPath;
        Value = LoadOrCreate(FilePath, logger);
    }

    private static string LoadOrCreate(string path, ILogger logger)
    {
        if (Read(path) is { } stored)
        {
            logger.LogDebug("Session signing key read from {Path}.", path);
            return stored;
        }

        string generated = Convert.ToBase64String(RandomNumberGenerator.GetBytes(KeyBytes));
        try
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } dir)
                Directory.CreateDirectory(dir);

            // CreateNew rather than Create, so two services starting together cannot each write a key
            // and leave the loser holding one the file does not contain.
            using FileStream file = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            // Narrowed through the handle before a single byte of the key is written. Creating the file
            // and chmod'ing it afterwards leaves the secret readable for however long the two calls are
            // apart, and the umask decides how wide that window is.
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(file.SafeFileHandle, OwnerOnly);
            file.Write(Encoding.UTF8.GetBytes(generated));
        }
        catch (IOException) when (Read(path) is { } raced)
        {
            // Something else on this host got there first. Its key is the host's key.
            return raced;
        }
        catch (Exception e)
        {
            logger.LogWarning(e,
                "Session signing key could not be written to {Path}, so this process is using one of "
                + "its own. Everyone signed in is signed out when this service restarts.", path);
            return generated;
        }

        logger.LogInformation("Session signing key generated at {Path}.", path);
        return generated;
    }

    /// <summary>The stored key, or <see langword="null"/> when there is no readable one.</summary>
    private static string? Read(string path)
    {
        try
        {
            // Trimmed because the file is one an operator may well have written by hand, and a
            // trailing newline that silently changed the key would be a very quiet way to sign
            // everyone out.
            string text = File.ReadAllText(path).Trim();
            return text.Length == 0 ? null : text;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
