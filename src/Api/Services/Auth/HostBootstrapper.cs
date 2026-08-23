namespace TheKrystalShip.Api.Services.Auth;

/// <summary>
/// Settles, on every start, the two things a host needs before anybody can sign in: the key sessions
/// are signed with, and an account to sign in as.
/// </summary>
/// <remarks>
/// Both are settled here rather than at the first request, so a host that has never been opened in a
/// browser still has them — the key file exists from the start that generated it, and the password an
/// operator has to go and read is waiting before they go looking. On every later start both are
/// already there and this does nothing at all.
/// </remarks>
internal sealed class HostBootstrapper(
    ApiOptions options,
    UserDirectory users,
    HostSigningKey signingKey,
    ILogger<HostBootstrapper> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        // Asking for the key is what makes it exist: it is built on demand, and on a host nobody signs
        // in to, the file it is kept in would otherwise never be written.
        if (signingKey.FilePath is { } keyPath)
            logger.LogDebug("Sessions on this host are signed with the key at {Path}.", keyPath);

        // Nothing to bootstrap into. UserDirectory has already said why, loudly.
        if (!users.Available)
            return;

        string? password;
        try
        {
            password = await FirstAdmin.CreateAsync(
                users.Store, users.SignIn, FirstAdmin.DefaultUsername, ct);
        }
        catch (Exception e)
        {
            // A host with no administrator is a host nobody can administer, which is worth an error —
            // but not worth refusing to start, because the endpoints that report the problem are on
            // this same service.
            logger.LogError(e, "The first administrator could not be created in the KGSM account store.");
            return;
        }

        // Accounts already exist, which is every start but the first.
        if (password is null)
            return;

        string path = options.InitialAdminPasswordPath;
        if (FirstAdmin.WritePasswordFile(path, FirstAdmin.DefaultUsername, password, logger))
        {
            logger.LogInformation(
                "This host had no accounts, so the administrator '{Username}' was created. Its one-time "
                + "password is in {Path} — read it, sign in, and change it; the file is removed on that "
                + "first sign-in.", FirstAdmin.DefaultUsername, path);
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
