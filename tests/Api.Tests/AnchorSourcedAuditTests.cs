using System.Text;
using System.Text.Json;

using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.KGSM.Auth.Journal;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Account events written somewhere else, read back here.
/// </summary>
/// <remarks>
/// <para>
/// A cluster whose accounts are held by an auth anchor has the anchor record every sign-in, because
/// it is the only thing that witnesses one. Those lines land in the anchor's own journal, which this
/// API reads beside every other producer's — so the shaping has to be blind to which journal a line
/// came from, and the wording has to be the same one an operator has always read.
/// </para>
/// <para>
/// The payloads here are built from <see cref="AuthEventPayloads"/> — the same writer the anchor
/// calls — and then deserialized the way the reader does. A test spelling the JSON by hand would
/// assert that a shape this file invented round-trips, which is exactly the agreement that matters
/// and exactly the one it would not be checking.
/// </para>
/// </remarks>
public sealed class AnchorSourcedAuditTests
{
    private const string HostId = "hotrod";
    private static readonly DateTimeOffset When = DateTimeOffset.Parse("2026-08-30T23:21:13Z");

    /// <summary>Serialize a payload the way a producer writes it, and read it the way a reader does.</summary>
    private static T Roundtrip<T>(Action<Utf8JsonWriter> payload)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            payload(writer);
            writer.WriteEndObject();
        }

        return JsonSerializer.Deserialize<T>(
            Encoding.UTF8.GetString(buffer.ToArray()),
            new JsonSerializerOptions(JsonSerializerDefaults.General))!;
    }

    private static T Stamped<T>(T data, string actor) where T : KgsmEventDataBase
    {
        data.Timestamp = When;
        data.Actor = actor;
        data.Origin = AuditOrigin.Ui;
        return data;
    }

    // ---- the shapes agree ----------------------------------------------------------------------

    [Fact]
    public void A_sign_in_the_anchor_wrote_reads_back_whole()
    {
        AuthSessionEventData data = Roundtrip<AuthSessionEventData>(
            AuthEventPayloads.Session(
                userId: "usr_abc", username: "haru", identity: "discord:245717107596197888",
                provider: "discord", tier: "admin", sid: "sid_1", userAgent: "Firefox",
                peerNode: null));

        // Every field, because the failure this guards is silent: a name spelled differently by one
        // writer deserializes to null and the row renders with something missing and nothing
        // reported.
        Assert.Equal("usr_abc", data.UserId);
        Assert.Equal("haru", data.Username);
        Assert.Equal("discord:245717107596197888", data.Identity);
        Assert.Equal("discord", data.Provider);
        Assert.Equal("admin", data.Tier);
        Assert.Equal("sid_1", data.Sid);
        Assert.Equal("Firefox", data.UserAgent);
        Assert.Null(data.PeerNode);
    }

    [Fact]
    public void An_account_change_the_anchor_wrote_reads_back_whole()
    {
        UserAccountEventData data = Roundtrip<UserAccountEventData>(
            AuthEventPayloads.Account(
                userId: "usr_target", username: "someone", fromTier: "none", toTier: "operator",
                fromStatus: "pending", toStatus: "active", byHolder: null));

        Assert.Equal("usr_target", data.UserId);
        Assert.Equal("someone", data.Username);
        Assert.Equal("none", data.FromTier);
        Assert.Equal("operator", data.ToTier);
        Assert.Equal("pending", data.FromStatus);
        Assert.Equal("active", data.ToStatus);

        // A real null rather than false. "The distinction does not apply" and "an administrator did
        // it" are different facts, and reading the first as the second reports every provisioning as
        // somebody else's doing.
        Assert.Null(data.ByHolder);
    }

    [Fact]
    public void An_identity_link_the_anchor_wrote_reads_back_whole()
    {
        IdentityLinkEventData data = Roundtrip<IdentityLinkEventData>(
            AuthEventPayloads.Identity(
                userId: "usr_abc", username: "haru", provider: "discord",
                handle: "discord:245717107596197888"));

        Assert.Equal("usr_abc", data.UserId);
        Assert.Equal("haru", data.Username);
        Assert.Equal("discord", data.Provider);
        Assert.Equal("discord:245717107596197888", data.Handle);
    }

    // ---- the wording is unchanged --------------------------------------------------------------

    [Fact]
    public void A_sign_in_reads_the_same_whichever_producer_recorded_it()
    {
        AuthSessionEventData data = Stamped(
            Roundtrip<AuthSessionEventData>(AuthEventPayloads.Session(
                "usr_abc", "haru", "discord:haru", "discord", "admin", "sid_1", null, null)),
            "discord:haru");

        AuditWrite row = AuditMapping.FromAuthSessionEvent(data, AuthEvents.SignedIn, HostId);

        // The sentence a reader has always seen. It comes from the PROVIDER on the record, not from
        // which code path ran — which is what lets the same wording cover a row written before an
        // anchor existed and one written after.
        Assert.Equal("haru logged in", row.Summary);
        Assert.Equal(AuditSeverity.Info, row.Severity);
        Assert.Equal("haru", row.Actor.Name);
        Assert.Equal(When, row.Ts);
    }

    [Fact]
    public void A_password_sign_in_is_told_apart_from_a_provider_one()
    {
        AuthSessionEventData data = Stamped(
            Roundtrip<AuthSessionEventData>(AuthEventPayloads.Session(
                "usr_abc", "haru", "local:usr_abc", "local", "viewer", "sid_1", null, null)),
            "local:haru");

        // A reader auditing access wants a local credential and a bounce through an external provider
        // apart, and the provider is what says which.
        Assert.Equal(
            "haru signed in with a password",
            AuditMapping.FromAuthSessionEvent(data, AuthEvents.SignedIn, HostId).Summary);
    }

    // ---- the run of guesses --------------------------------------------------------------------

    [Fact]
    public void A_lockout_is_the_one_auth_row_that_reads_as_danger()
    {
        DateTimeOffset until = When.AddMinutes(5);

        AuthLockedOutData data = Stamped(
            Roundtrip<AuthLockedOutData>(AuthEventPayloads.LockedOut(
                userId: "usr_abc", username: "haru", identity: "local:usr_abc",
                failedCount: 6, until: until)),
            "local:haru");

        AuditWrite row = AuditMapping.FromLockedOutEvent(data, HostId);

        // Every other auth row records something that worked. This one is somebody working through
        // passwords against an account that exists, and it is what an access review is looking for.
        Assert.Equal(AuditSeverity.Danger, row.Severity);

        // The count is in the sentence, because "locked out" alone reads like somebody mistyping.
        Assert.Equal("'haru' was locked out after 6 failed sign-ins", row.Summary);
        Assert.Equal("6", row.Meta!["failedCount"]);
        Assert.Equal("usr_abc", row.Meta["userId"]);
        Assert.Equal(until.ToString("O"), row.Meta["until"]);
    }

    [Fact]
    public void A_lockout_row_carries_no_password_and_no_attempt()
    {
        AuthLockedOutData data = Stamped(
            Roundtrip<AuthLockedOutData>(AuthEventPayloads.LockedOut(
                "usr_abc", "haru", "local:usr_abc", 6, When.AddMinutes(5))),
            "local:haru");

        AuditWrite row = AuditMapping.FromLockedOutEvent(data, HostId);

        // What was TRIED is not part of the fact that an account was locked, and a near miss on the
        // record is a near miss anybody who can read the page now holds.
        string rendered = row.Summary + string.Join(' ', row.Meta!.Select(kv => $"{kv.Key}={kv.Value}"));
        Assert.DoesNotContain("password", rendered, StringComparison.OrdinalIgnoreCase);
    }

    // ---- a session withdrawn rather than ended -------------------------------------------------

    [Fact]
    public void A_session_withdrawn_with_its_account_names_no_actor()
    {
        AuthSessionRevokedData data = Stamped(
            Roundtrip<AuthSessionRevokedData>(AuthEventPayloads.SessionRevoked(
                scope: SessionRevokeScopes.Withdrawn, userId: "usr_abc", username: "haru",
                sid: "sid_1", count: 1)),
            "system:auth-anchor");

        AuditWrite row = AuditMapping.FromSessionRevokedEvent(data, HostId);

        // Nobody acted at the moment it happened. The disable is already its own row and can be hours
        // earlier; this is when the access actually stopped, and naming a person would name one who
        // was not there.
        Assert.Equal("a session belonging to haru ended: the account is switched off", row.Summary);
        Assert.Equal(AuditSeverity.Warn, row.Severity);
        Assert.Equal(SessionRevokeScopes.Withdrawn, row.Meta!["scope"]);
    }

    [Fact]
    public void The_scopes_a_person_drove_still_read_as_theirs()
    {
        AuthSessionRevokedData data = Stamped(
            Roundtrip<AuthSessionRevokedData>(AuthEventPayloads.SessionRevoked(
                SessionRevokeScopes.Admin, "usr_target", "someone", "sid_1", 1)),
            "discord:admin");

        Assert.Equal(
            "admin revoked a session belonging to someone",
            AuditMapping.FromSessionRevokedEvent(data, HostId).Summary);
    }

    // ---- the reader dispatches on the type alone -----------------------------------------------

    [Theory]
    [InlineData(AuthEvents.SignedIn)]
    [InlineData(AuthEvents.SignedOut)]
    [InlineData(AuthEvents.SessionRevoked)]
    [InlineData(AuthEvents.LockedOut)]
    [InlineData(AuthEvents.UserProvisioned)]
    [InlineData(AuthEvents.UserApproved)]
    [InlineData(AuthEvents.UserDisabled)]
    [InlineData(AuthEvents.UserTierChanged)]
    [InlineData(AuthEvents.UserDeleted)]
    [InlineData(AuthEvents.UserPasswordChanged)]
    [InlineData(AuthEvents.IdentityLinked)]
    [InlineData(AuthEvents.IdentityUnlinked)]
    public void Every_type_the_anchor_writes_becomes_a_row(string type)
    {
        // The anchor's whole vocabulary, driven through the reader an audit page actually calls. A
        // type it emits that nothing here maps renders nowhere, and the failure is silent: the line
        // is on disk and the page is simply short of a row.
        AuditRecord? row = EngineEventShaping.Shape(
            new EventHistoryEntry(
                Id: "evt_1", Ts: When, Type: type, Instance: null, Blueprint: null,
                Actor: "discord:admin", Origin: AuditOrigin.Ui, Hostname: "hotrod",
                Data: PayloadFor(type), Producer: "kgsm-auth-anchor"),
            HostId);

        Assert.NotNull(row);
        Assert.Equal(type, row.Action);
        Assert.False(string.IsNullOrWhiteSpace(row.Summary));

        // Declared in the shared catalog too, so a consumer that classifies fields by type — deciding
        // what is personal and what may be shown — has an answer for it rather than a default.
        Assert.True(KgsmEventCatalog.Describe(type).Known);
    }

    /// <summary>A payload of the right shape for <paramref name="type"/>, from the writer itself.</summary>
    private static JsonElement PayloadFor(string type)
    {
        Action<Utf8JsonWriter> payload = type switch
        {
            AuthEvents.SignedIn or AuthEvents.SignedOut or AuthEvents.ClusterVouched =>
                AuthEventPayloads.Session(
                    "usr_abc", "haru", "discord:haru", "discord", "admin", "sid_1", null, null),

            AuthEvents.SessionRevoked => AuthEventPayloads.SessionRevoked(
                SessionRevokeScopes.Withdrawn, "usr_abc", "haru", "sid_1", 1),

            AuthEvents.LockedOut => AuthEventPayloads.LockedOut(
                "usr_abc", "haru", "local:usr_abc", 6, When.AddMinutes(5)),

            AuthEvents.IdentityLinked or AuthEvents.IdentityUnlinked =>
                AuthEventPayloads.Identity("usr_abc", "haru", "discord", "discord:haru"),

            _ => AuthEventPayloads.Account(
                "usr_target", "someone", "none", "operator", "pending", "active", null),
        };

        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            payload(writer);
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(buffer.ToArray()).RootElement.Clone();
    }
}
