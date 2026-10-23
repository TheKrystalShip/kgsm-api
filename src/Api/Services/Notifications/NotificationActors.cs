using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Auth;
using TheKrystalShip.Auth.Cluster;

namespace TheKrystalShip.Api.Services.Notifications;

/// <summary>
/// Who a notification's person is, from both ends: the person a request is made by, and the audit actor
/// a button tap acts as.
/// </summary>
/// <remarks>
/// <para>
/// A person is the account id the node's replica knows them by (the synthetic caller's actor on a host
/// with auth off). The replica names an account by a username and holds no provider, but an audit actor
/// is <c>provider:name</c>, so the actor a signed-in person presented is remembered beside the library's
/// state. A button can only be tapped from a device its person subscribed through a signed-in route,
/// which is where the actor is recorded; a person with none recorded is refused at the tap rather than
/// attributed to a provider they never signed in with.
/// </para>
/// </remarks>
public sealed class NotificationActors(ApiOptions options, ILogger<NotificationActors> logger)
{
    private readonly Lock _gate = new();
    private ConcurrentDictionary<string, string>? _actors;

    private string FilePath => Path.Combine(options.NotificationsDir, "actors.json");

    /// <summary>The person a signed-in request is made by, or null when there is none.</summary>
    public string? PersonOf(HttpContext http)
    {
        ClaimsPrincipal user = http.User;
        if (user.Identity is not ClaimsIdentity ci || SessionClaims.ReadIdentity(ci) is not { } identity)
            return null;

        string? person = options.AuthDisabled ? options.DisabledAuthActor : NodeAccess.AccountOf(user);
        if (person is null)
            return null;

        Remember(person, identity.ActorString);
        return person;
    }

    /// <summary>The audit actor <paramref name="person"/> last presented, or null when none is known.</summary>
    public string? ActorOf(string person)
    {
        if (options.AuthDisabled && person == options.DisabledAuthActor)
            return person;

        return Actors().TryGetValue(person, out string? actor) ? actor : null;
    }

    private void Remember(string person, string actor)
    {
        ConcurrentDictionary<string, string> actors = Actors();
        if (actors.TryGetValue(person, out string? known) && known == actor)
            return;

        actors[person] = actor;
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(options.NotificationsDir);
                string temp = FilePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(actors));
                File.Move(temp, FilePath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "could not record the actor of a notification person in {Path}", FilePath);
            }
        }
    }

    private ConcurrentDictionary<string, string> Actors()
    {
        lock (_gate)
        {
            if (_actors is not null)
                return _actors;

            try
            {
                if (File.Exists(FilePath)
                    && JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) is { } read)
                    return _actors = new ConcurrentDictionary<string, string>(read, StringComparer.Ordinal);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "could not read {Path}; actors are learned again as people sign in", FilePath);
            }

            return _actors = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        }
    }
}
