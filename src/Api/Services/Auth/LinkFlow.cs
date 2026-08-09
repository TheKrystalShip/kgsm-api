using System.Collections.Concurrent;

using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Discord;

namespace TheKrystalShip.Api.Services.Auth;

/// <summary>
/// When each session last proved a credential.
/// </summary>
/// <remarks>
/// <para>
/// Holding a session is not the same as having proved you are its owner. The two come apart exactly
/// where it matters: an unlocked laptop, a browser left signed in, a token lifted from storage. Most
/// of what a session does is bounded by its own life, so the distinction costs nothing — but attaching
/// an identity outlives it, because afterwards whoever holds that provider account can sign in as this
/// one forever. So that one write asks for the credential again.
/// </para>
/// <para>
/// <b>Signing in counts as proving it.</b> A password login and an OAuth callback both stamp the
/// session they mint, so someone who has just arrived links without being asked for anything, and
/// someone returning to a week-old tab is asked once.
/// </para>
/// <para>
/// In memory, deliberately. A restart makes every session prove itself again, which is the safe
/// direction to fail in, and the alternative — a column on the session row — would persist "recently
/// proved" across exactly the events that should end it.
/// </para>
/// </remarks>
public sealed class ReauthGate(TimeSpan window)
{
    // Sessions are per-browser and expire, so this stays small; the sweep is only insurance against a
    // host that mints far more than it ever revokes.
    private const int SweepAbove = 512;

    private readonly ConcurrentDictionary<string, DateTimeOffset> _proved = new(StringComparer.Ordinal);

    /// <summary>How long a proof lasts.</summary>
    public TimeSpan Window { get; } = window > TimeSpan.Zero ? window : TimeSpan.FromMinutes(5);

    /// <summary>Record that this session's owner has just proved a credential.</summary>
    public void Stamp(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return;

        DateTimeOffset now = DateTimeOffset.UtcNow;
        _proved[sessionId] = now;

        if (_proved.Count > SweepAbove)
        {
            foreach (KeyValuePair<string, DateTimeOffset> entry in _proved)
            {
                if (now - entry.Value > Window)
                    _proved.TryRemove(entry.Key, out _);
            }
        }
    }

    /// <summary>Until when this session may change what proves it, or <see langword="null"/>.</summary>
    public DateTimeOffset? FreshUntil(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId) || !_proved.TryGetValue(sessionId, out DateTimeOffset at))
            return null;

        DateTimeOffset until = at + Window;
        if (until <= DateTimeOffset.UtcNow)
        {
            _proved.TryRemove(sessionId, out _);
            return null;
        }

        return until;
    }

    /// <summary>Whether this session has proved a credential recently enough.</summary>
    public bool IsFresh(string? sessionId) => FreshUntil(sessionId) is not null;

    /// <summary>Drop a session's proof — what a logout or a revoke calls.</summary>
    public void Forget(string? sessionId)
    {
        if (!string.IsNullOrEmpty(sessionId))
            _proved.TryRemove(sessionId, out _);
    }
}

/// <summary>A link in flight: which account started it, and the handshake it must come back with.</summary>
/// <param name="UserId">The account the arriving identity will be attached to.</param>
/// <param name="SessionId">The session that started it, so the callback can check it is still fresh.</param>
/// <param name="Handshake">The <c>state</c> Discord echoes back and the PKCE verifier.</param>
/// <param name="Expires">When this ticket stops being redeemable.</param>
public sealed record LinkTicket(
    string UserId, string SessionId, OAuthHandshake Handshake, DateTimeOffset Expires);

/// <summary>
/// The links that have been started and not yet come back.
/// </summary>
/// <remarks>
/// <para>
/// A login can be stateless — the state and verifier ride an HttpOnly cookie and the callback needs
/// nothing else. A <em>link</em> cannot: it has to know which account to attach the arriving identity
/// to, and the callback is a top-level navigation from Discord that carries no bearer. Putting the
/// account id in the cookie would make the browser the authority on whose account is being changed, so
/// the cookie carries an opaque ticket instead and the account stays here.
/// </para>
/// <para>
/// Single-use and short-lived: redeeming removes the ticket, so a callback replayed from history or a
/// log attaches nothing. In memory for the same reason as <see cref="ReauthGate"/> — a restart drops
/// links in flight, which costs a click and cannot grant anything.
/// </para>
/// </remarks>
public sealed class LinkTicketStore
{
    /// <summary>How long a browser has to finish a link it started.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    private const int SweepAbove = 256;

    private readonly ConcurrentDictionary<string, LinkTicket> _tickets = new(StringComparer.Ordinal);

    /// <summary>Start a link, returning the opaque ticket the browser carries in its cookie.</summary>
    public string Issue(string userId, string sessionId, OAuthHandshake handshake)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string ticketId = Guid.NewGuid().ToString("N");
        _tickets[ticketId] = new LinkTicket(userId, sessionId, handshake, now + Ttl);

        if (_tickets.Count > SweepAbove)
        {
            foreach (KeyValuePair<string, LinkTicket> entry in _tickets)
            {
                if (entry.Value.Expires <= now)
                    _tickets.TryRemove(entry.Key, out _);
            }
        }

        return ticketId;
    }

    /// <summary>
    /// Take a ticket back, if it exists, has not expired, and the state Discord echoed matches the one
    /// it was issued with. Removes it either way — a ticket is worth one attempt.
    /// </summary>
    public LinkTicket? Redeem(string? ticketId, string? state)
    {
        if (string.IsNullOrEmpty(ticketId) || !_tickets.TryRemove(ticketId, out LinkTicket? ticket))
            return null;

        return ticket.Expires > DateTimeOffset.UtcNow && ticket.Handshake.MatchesState(state)
            ? ticket
            : null;
    }
}
