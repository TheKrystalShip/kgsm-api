using System.Text.Json.Serialization;

namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// <c>GET /me</c> — the caller's own identity, the granted <c>scopes</c>, the recent sign-ins and the
/// account's state. What the caller may do is <c>GET /me/access</c>, which the SPA gates its controls on.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read-only.</b> Anything that must follow a person across devices is a preference, and preferences
/// live at <c>/me/preferences</c>. So <c>/me</c> surfaces only what the bearer already carries plus what
/// this node resolves about it: <c>display</c>/<c>username</c> are the snapshot the auth anchor stamped
/// into the session when it was minted, never a guessed label and never a fresh fetch from a provider.
/// </para>
/// <para>
/// <b><c>recentLogins</c> is the one read that is not the bearer.</b> It comes from the merged audit
/// feed, where the auth anchor's journal records each sign-in, so it answers "what happened recently",
/// including sessions since ended. There is no <c>lastLogin</c> column and no user row anywhere.
/// </para>
/// <para>
/// <b><c>status</c> tells apart two people who can do nothing.</b> Someone awaiting approval and someone
/// nothing here has ever heard of both hold no access, and a panel owes them different sentences — one
/// is being told to wait, the other that this is not their cluster.
/// </para>
/// </remarks>
/// <param name="Status">
/// The state of the KGSM account behind the caller: <c>active</c>, <c>pending</c>, <c>disabled</c>, or
/// <c>unknown</c> when this identity proves no account here or the replica cannot be read.
/// </param>
public sealed record MeResponse(SessionUser User, IReadOnlyList<string> Scopes,
    IReadOnlyList<RecentLogin> RecentLogins, string Status);

/// <summary>
/// Who a session names — the provider-qualified handle, the username, the display name and the avatar
/// the auth anchor stamped into it.
/// </summary>
public sealed record SessionUser(
    string Id,
    string Username,
    string Display,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AvatarUrl);

/// <summary>
/// The mutable half of <see cref="MeResponse"/>, pushed on the <c>me</c> stream topic as
/// <c>me.patch</c>: what state the caller's own account is in.
/// </summary>
/// <remarks>
/// <c>status</c> carries the vocabulary <c>GET /me</c> answers in, so a client merges a frame straight
/// over what it hydrated with no second mapping. The identity half is left out because nothing on this
/// path changes it; access changes arrive as <c>me.access</c>.
/// </remarks>
public sealed record MeStanding(string Status);

/// <summary>One sign-in, shaped for the <c>/me</c> recent-logins list. <c>Device</c> is the
/// <c>User-Agent</c> the sign-in arrived with, or <see langword="null"/> when the caller sent none
/// (honest-unknown, never a guessed label).</summary>
public sealed record RecentLogin(DateTimeOffset Ts, string? Device);
