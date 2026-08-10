using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace TheKrystalShip.Api.Services.Backups;

/// <summary>
/// One minted permission to download one backup archive.
/// </summary>
/// <param name="ServerId">The instance the backup belongs to.</param>
/// <param name="BackupId">The single backup this ticket authorises — and no other.</param>
/// <param name="Actor">Who minted it, carried so the audit row at redemption names them rather than
/// the anonymous request that redeems it.</param>
/// <param name="Origin">The surface that asked for it.</param>
/// <param name="SessionId">The session that minted it, for correlation.</param>
/// <param name="ExpiresAt">When it stops being redeemable.</param>
public sealed record BackupDownloadTicket(
    string ServerId,
    string BackupId,
    string? Actor,
    string? Origin,
    string? SessionId,
    DateTimeOffset ExpiresAt);

/// <summary>
/// The short-lived tickets that let a browser download a backup archive by plain navigation.
/// </summary>
/// <remarks>
/// <para><strong>Why a ticket at all.</strong> Every other call in this API authenticates with a bearer
/// header, which a <c>fetch</c> can set and a navigation cannot. Downloading through <c>fetch</c> means
/// buffering the whole archive in browser memory before the save dialog appears — survivable at 90&#160;MB,
/// fatal at several GB, and a backup has no upper bound. A ticket in the URL is what lets the browser
/// stream straight to disk with its own progress and resume behaviour.</para>
///
/// <para><strong>What that costs, stated plainly.</strong> A URL is not a header: it reaches browser
/// history, and can reach a referrer or a proxy log. The ticket is therefore scoped as narrowly as it can
/// be while still working — it authorises exactly one backup of one server, it expires in minutes, and it
/// is worthless afterwards. It is a bearer credential for that window; anyone holding it inside the
/// window can fetch that one archive. That is the trade the transport requires, not an oversight.</para>
///
/// <para><strong>Why not strictly single-use.</strong> A resumed or ranged download is a SECOND request
/// for the same bytes — a browser retrying after a network blip re-issues with a <c>Range</c> header.
/// Burning the ticket on first contact would make exactly the resumability that justified this design
/// impossible, so a ticket is redeemable repeatedly until it expires. The audit row is written once, on
/// first redemption, so one download is one row rather than one row per TCP hiccup.</para>
///
/// <para>Storage is deliberately in-memory: a ticket outliving the process that minted it has no value,
/// since the whole point is a window measured in minutes. An API restart invalidates every outstanding
/// ticket, and the SPA's answer is to mint another — no migration, no persistence, nothing to clean up.
/// </para>
/// </remarks>
public sealed class BackupDownloadTickets
{
    /// <summary>
    /// How long a ticket stays redeemable. Long enough to cover the round trip from mint to the
    /// browser actually starting the transfer (plus an immediate retry), short enough that a URL which
    /// leaks into history or a log is inert by the time anyone reads it. It bounds STARTING a download,
    /// not finishing one — an established transfer streams for as long as it needs, because the ticket
    /// is checked once per request rather than continuously.
    /// </summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, Entry> _tickets = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    /// <summary>Initializes a new instance of the <see cref="BackupDownloadTickets"/> class.</summary>
    /// <param name="time">Clock source — injected so tests can expire a ticket without sleeping.</param>
    public BackupDownloadTickets(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    private sealed class Entry
    {
        public required BackupDownloadTicket Ticket { get; init; }

        // 0 until the first redemption wins the race to set it — the audit-once latch.
        public int Audited;
    }

    /// <summary>Mints a ticket for one backup and returns its opaque handle.</summary>
    /// <param name="serverId">The instance the backup belongs to.</param>
    /// <param name="backupId">The backup being authorised.</param>
    /// <param name="actor">Who is minting it.</param>
    /// <param name="origin">The surface driving the request.</param>
    /// <param name="sessionId">The minting session, for correlation.</param>
    /// <returns>The handle and the ticket it maps to.</returns>
    public (string Handle, BackupDownloadTicket Ticket) Mint(
        string serverId, string backupId, string? actor, string? origin, string? sessionId)
    {
        Sweep();

        // 128 bits from a CSPRNG, hex — the ecosystem's handle shape. Unguessable is the only property
        // that matters here: the handle IS the credential for its window.
        string handle = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var ticket = new BackupDownloadTicket(
            serverId, backupId, actor, origin, sessionId, _time.GetUtcNow().Add(Ttl));

        _tickets[handle] = new Entry { Ticket = ticket };
        return (handle, ticket);
    }

    /// <summary>
    /// Redeems a handle for the backup it authorises.
    /// </summary>
    /// <param name="handle">The handle from the URL.</param>
    /// <param name="serverId">The server the request is for — must match what was minted.</param>
    /// <param name="backupId">The backup the request is for — must match what was minted.</param>
    /// <param name="ticket">The ticket, when valid.</param>
    /// <param name="firstRedemption">
    /// True exactly once per ticket, on whichever request wins the race — the caller writes the audit
    /// row only then, so a resumed download does not log twice.
    /// </param>
    /// <returns><c>true</c> when the handle is live and authorises precisely this backup.</returns>
    public bool TryRedeem(string? handle, string serverId, string backupId,
        out BackupDownloadTicket? ticket, out bool firstRedemption)
    {
        ticket = null;
        firstRedemption = false;

        if (string.IsNullOrWhiteSpace(handle)) return false;
        if (!_tickets.TryGetValue(handle, out Entry? entry)) return false;

        if (_time.GetUtcNow() >= entry.Ticket.ExpiresAt)
        {
            _tickets.TryRemove(handle, out _);
            return false;
        }

        // A ticket names ONE backup. Redeeming it against a different server or backup is refused rather
        // than honoured for whatever the URL happens to say — otherwise a ticket for a small backup an
        // operator may download would serve as a ticket for any other, which is the whole authorisation.
        if (!string.Equals(entry.Ticket.ServerId, serverId, StringComparison.Ordinal)
            || !string.Equals(entry.Ticket.BackupId, backupId, StringComparison.Ordinal))
            return false;

        firstRedemption = Interlocked.Exchange(ref entry.Audited, 1) == 0;
        ticket = entry.Ticket;
        return true;
    }

    /// <summary>Drops expired tickets. Called on mint — the map only grows when someone mints.</summary>
    private void Sweep()
    {
        DateTimeOffset now = _time.GetUtcNow();
        foreach (KeyValuePair<string, Entry> kvp in _tickets)
        {
            if (now >= kvp.Value.Ticket.ExpiresAt) _tickets.TryRemove(kvp.Key, out _);
        }
    }
}
