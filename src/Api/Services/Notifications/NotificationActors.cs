using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using TheKrystalShip.Api.Services.Auth;

namespace TheKrystalShip.Api.Services.Notifications;

/// <summary>
/// What a notification's person last presented: the audit actor they act as, the name they go by and the
/// actions their token listed, as of <paramref name="SeenAt"/>.
/// </summary>
public sealed record KnownPerson(string Actor, string DisplayName, IReadOnlyList<string> Actions, DateTimeOffset SeenAt)
{
    /// <summary>Whether the person held <paramref name="action"/> when last seen.</summary>
    public bool Held(string action) => Actions.Contains(action, StringComparer.Ordinal);
}

/// <summary>
/// Who a notification's person is, from both ends: the person a request is made by, and the actor and
/// access a notification is sent for or a button tap acts as.
/// </summary>
/// <remarks>
/// <para>
/// A person is the subject of the token they presented (the synthetic caller's actor on a host with auth
/// off). A notification is told and a button is tapped when its person is not here with a token, so what
/// the person may do is what their token last listed, remembered beside the library's state every time a
/// signed-in request names them. That memory is bounded by <see cref="Freshness"/>: a person not seen
/// within it is told nothing and acts on nothing, so a revoked role stops reaching a phone within that
/// window even when its owner never opens the panel again.
/// </para>
/// </remarks>
public sealed class NotificationActors(ApiOptions options, TimeProvider time, ILogger<NotificationActors> logger)
{
    /// <summary>How long what a person's token listed is trusted for a notification sent or tapped without one.</summary>
    public static readonly TimeSpan Freshness = TimeSpan.FromDays(7);

    private readonly Lock _gate = new();
    private ConcurrentDictionary<string, KnownPerson>? _people;

    private string FilePath => Path.Combine(options.NotificationsDir, "people.json");

    /// <summary>The person a signed-in request is made by, or null when there is none.</summary>
    public string? PersonOf(HttpContext http)
    {
        if (CallerId.Of(http.User) is not { Kind: CallerKind.Person or CallerKind.Open } caller)
            return null;

        IReadOnlyList<string> held = http.RequestServices.GetRequiredService<NodeAccess>().HeldList(http.User);
        Remember(caller.Subject, new KnownPerson(caller.Actor, caller.DisplayName, held, time.GetUtcNow()));
        return caller.Subject;
    }

    /// <summary>What <paramref name="person"/> last presented, or null when unknown or not seen within <see cref="Freshness"/>.</summary>
    public KnownPerson? Known(string person)
    {
        if (options.AuthDisabled && person == options.DisabledAuthActor)
            return new KnownPerson(person, person, [], time.GetUtcNow());

        return People().TryGetValue(person, out KnownPerson? known) && time.GetUtcNow() - known.SeenAt <= Freshness
            ? known
            : null;
    }

    /// <summary>Everyone seen within <see cref="Freshness"/> who held <paramref name="action"/>.</summary>
    public IReadOnlyList<string> Holding(string action)
    {
        if (options.AuthDisabled)
            return [options.DisabledAuthActor];

        DateTimeOffset now = time.GetUtcNow();
        return [.. People().Where(p => now - p.Value.SeenAt <= Freshness && p.Value.Held(action)).Select(p => p.Key)];
    }

    /// <summary>Whether <paramref name="person"/> may perform <paramref name="action"/>: they held it when last seen, recently enough.</summary>
    public bool Allows(string person, string action) =>
        options.AuthDisabled ? person == options.DisabledAuthActor : Known(person)?.Held(action) == true;

    private void Remember(string person, KnownPerson known)
    {
        ConcurrentDictionary<string, KnownPerson> people = People();
        if (people.TryGetValue(person, out KnownPerson? previous)
            && previous.Actor == known.Actor
            && previous.DisplayName == known.DisplayName
            && previous.Actions.SequenceEqual(known.Actions, StringComparer.Ordinal)
            && known.SeenAt - previous.SeenAt < TimeSpan.FromHours(1))
        {
            return;
        }

        people[person] = known;
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(options.NotificationsDir);
                string temp = FilePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(
                    new Dictionary<string, KnownPerson>(people, StringComparer.Ordinal),
                    NotificationPeopleJson.Default.DictionaryStringKnownPerson));
                File.Move(temp, FilePath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "could not record a notification person in {Path}", FilePath);
            }
        }
    }

    private ConcurrentDictionary<string, KnownPerson> People()
    {
        lock (_gate)
        {
            if (_people is not null)
                return _people;

            try
            {
                if (File.Exists(FilePath)
                    && JsonSerializer.Deserialize(File.ReadAllText(FilePath), NotificationPeopleJson.Default.DictionaryStringKnownPerson) is { } read)
                    return _people = new ConcurrentDictionary<string, KnownPerson>(read, StringComparer.Ordinal);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "could not read {Path}; people are learned again as they use the panel", FilePath);
            }

            return _people = new ConcurrentDictionary<string, KnownPerson>(StringComparer.Ordinal);
        }
    }
}

[JsonSerializable(typeof(Dictionary<string, KnownPerson>))]
internal sealed partial class NotificationPeopleJson : JsonSerializerContext;
