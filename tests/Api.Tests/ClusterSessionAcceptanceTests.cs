using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;

using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Sessions;
using TheKrystalShip.KGSM.Auth.Users;
using TheKrystalShip.KGSM.Auth.Cluster;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// A session this node did not mint, accepted because it can verify what it cannot produce.
/// </summary>
/// <remarks>
/// <para>
/// This is what one sign-in for a whole cluster comes down to on the receiving end. The anchor holds
/// a private key and mints a session audienced to the cluster; this node holds only the public half,
/// so it can check the signature and can never issue one. Everything the session then resolves to —
/// the tier, whether the account is switched off — comes from this node's own replica, so nothing on
/// the request path leaves the machine.
/// </para>
/// <para>
/// The anchor is stood in for by a signer and a stub of what gossip would have delivered, because
/// what is under test is this node's half of the contract. The other half has its own tests where it
/// is written.
/// </para>
/// </remarks>
public sealed class ClusterSessionAcceptanceTests
{
    private const string ClusterId = "test-cluster";

    /// <summary>
    /// The anchor's issuer, which is deliberately not this API's own ("kgsm-api"). Every surface
    /// stamps one of its own, so a node that assumed the two matched would refuse every session the
    /// cluster mints.
    /// </summary>
    private const string AnchorIssuer = "kgsm";

    /// <summary>What gossip would have delivered from the member holding the accounts.</summary>
    private sealed record Published(string? Audience, string? Issuer, IReadOnlyList<SecurityKey> Keys)
        : IClusterSessionKeys
    {
        public static Published Of(EcdsaSessionSigner signer, string audience = ClusterId) =>
            new(audience, AnchorIssuer, EcdsaSessionSigner.VerificationKeysFrom(signer.PublicKeys));

        public static Published Nothing => new(null, null, []);
    }

    /// <summary>A node told what the anchor publishes.</summary>
    private static WebApplicationFactory<Program> NodeKnowing(
        AuthTestFactory factory, IClusterSessionKeys published) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IClusterSessionKeys>();
            services.AddSingleton(published);
        }));

    /// <summary>The anchor's own token service: asymmetric, audienced to the cluster.</summary>
    private static SessionTokenService Anchor(EcdsaSessionSigner signer, string audience = ClusterId) =>
        new(new SessionTokenOptions(
                HostId: audience,
                SigningKey: "",
                AccessLifetime: TimeSpan.FromMinutes(15),
                RefreshLifetime: TimeSpan.FromDays(30),
                Issuer: AnchorIssuer),
            logger: null,
            signer: signer);

    /// <summary>
    /// Somebody the anchor knows, named by a replicated identity.
    /// </summary>
    /// <remarks>
    /// A provider-qualified handle is what actually travels: replication carries an account's external
    /// identities precisely so a session naming one resolves on a member that has never seen the
    /// person, and it carries no password — which is what makes a replica able to say what somebody
    /// may do and unable to let them in.
    /// </remarks>
    private static KgsmIdentity Somebody(string subject) =>
        new("discord", subject, subject, subject, null, []);

    private static HttpRequestMessage Get(string path, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    [Fact]
    public async Task A_session_the_anchor_minted_is_accepted_and_resolves_from_this_node_s_own_replica()
    {
        using var signer = EcdsaSessionSigner.Generate();
        await using var factory = new AuthTestFactory();
        using WebApplicationFactory<Program> node = NodeKnowing(factory, Published.Of(signer));
        using HttpClient client = node.CreateClient();

        // The account exists here because replication put it here, not because anybody signed in.
        KgsmIdentity person = Somebody("usr_replicated");
        AuthTestFactory.SetAccountOn(node.Services, person, KgsmTier.Operator);

        string token = Anchor(signer).MintAccess(person, KgsmTier.Admin, "sid_from_anchor").Token;

        HttpResponseMessage response = await client.SendAsync(Get("/api/v1/me", token));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        JsonElement me = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        // Operator, not the admin the token was minted with. Authority is what the replica says now,
        // and the tier claim is a display hint the resolver overwrites — the same rule a local
        // session is held to, which is what stops this node and the anchor disagreeing about a person.
        Assert.Equal("operator", me.GetProperty("tier").GetString());
    }

    [Fact]
    public async Task A_person_this_node_has_never_heard_of_holds_nothing_and_is_still_a_session()
    {
        using var signer = EcdsaSessionSigner.Generate();
        await using var factory = new AuthTestFactory();
        using WebApplicationFactory<Program> node = NodeKnowing(factory, Published.Of(signer));
        using HttpClient client = node.CreateClient();

        string token = Anchor(signer).MintAccess(Somebody("usr_stranger"), KgsmTier.Admin, "sid_1").Token;

        // A stranger is a real answer, so the session stands and every gate refuses them. Reporting
        // the signature as invalid would send somebody back to a sign-in that would work perfectly.
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Get("/api/v1/me", token))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Get("/api/v1/hosts", token))).StatusCode);
    }

    [Fact]
    public async Task A_disabled_account_ends_the_session_it_holds_here()
    {
        using var signer = EcdsaSessionSigner.Generate();
        await using var factory = new AuthTestFactory();
        using WebApplicationFactory<Program> node = NodeKnowing(factory, Published.Of(signer));
        using HttpClient client = node.CreateClient();

        KgsmIdentity person = Somebody("usr_switched_off");
        AuthTestFactory.SetAccountOn(node.Services, person, KgsmTier.Admin, UserStatus.Disabled);

        string token = Anchor(signer).MintAccess(person, KgsmTier.Admin, "sid_1").Token;

        // The switch is a door closing, not a tier being lowered — and it closes on a session minted
        // somewhere else, from a fact this node holds locally.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Get("/api/v1/me", token))).StatusCode);
    }

    [Fact]
    public async Task A_session_ended_over_the_bus_stops_being_accepted()
    {
        using var signer = EcdsaSessionSigner.Generate();
        await using var factory = new AuthTestFactory();
        using WebApplicationFactory<Program> node = NodeKnowing(factory, Published.Of(signer));
        using HttpClient client = node.CreateClient();

        KgsmIdentity person = Somebody("usr_signing_out");
        AuthTestFactory.SetAccountOn(node.Services, person, KgsmTier.Admin);

        const string sid = "sid_to_be_ended";
        string token = Anchor(signer).MintAccess(person, KgsmTier.Admin, sid).Token;
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Get("/api/v1/me", token))).StatusCode);

        // What the anchor's sign-out fan-out lands as here. The session has no row on this node, so
        // the marker is the whole of what ends it.
        var sessions = node.Services.GetRequiredService<SessionStore>();
        await sessions.RecordRevocationAsync(sid, "host-a", DateTimeOffset.UtcNow.AddDays(30));
        node.Services.GetRequiredService<ClusterSessionRevocations>().Evict(sid);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Get("/api/v1/me", token))).StatusCode);
    }

    [Fact]
    public async Task Ending_one_cluster_session_does_not_end_another()
    {
        using var signer = EcdsaSessionSigner.Generate();
        await using var factory = new AuthTestFactory();
        using WebApplicationFactory<Program> node = NodeKnowing(factory, Published.Of(signer));
        using HttpClient client = node.CreateClient();

        KgsmIdentity person = Somebody("usr_two_devices");
        AuthTestFactory.SetAccountOn(node.Services, person, KgsmTier.Admin);

        string phone = Anchor(signer).MintAccess(person, KgsmTier.Admin, "sid_phone").Token;
        string laptop = Anchor(signer).MintAccess(person, KgsmTier.Admin, "sid_laptop").Token;

        await node.Services.GetRequiredService<SessionStore>()
            .RecordRevocationAsync("sid_phone", "host-a", DateTimeOffset.UtcNow.AddDays(30));

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Get("/api/v1/me", phone))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Get("/api/v1/me", laptop))).StatusCode);
    }

    [Fact]
    public async Task A_node_that_has_heard_nothing_refuses_a_cluster_session_and_still_serves_its_own()
    {
        using var signer = EcdsaSessionSigner.Generate();
        await using var factory = new AuthTestFactory();
        using WebApplicationFactory<Program> node = NodeKnowing(factory, Published.Nothing);
        using HttpClient client = node.CreateClient();

        KgsmIdentity person = Somebody("usr_somebody");
        AuthTestFactory.SetAccountOn(node.Services, person, KgsmTier.Admin);
        string fromAnchor = Anchor(signer).MintAccess(person, KgsmTier.Admin, "sid_1").Token;

        Assert.Equal(
            HttpStatusCode.Unauthorized, (await client.SendAsync(Get("/api/v1/me", fromAnchor))).StatusCode);

        // The whole of what a standalone install has ever done, unchanged.
        string own = AuthTestFactory.MintTokenWithRow(node.Services, KgsmTier.Admin, access: true);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Get("/api/v1/me", own))).StatusCode);
    }

    [Fact]
    public async Task A_session_signed_by_a_key_this_node_was_never_given_is_refused()
    {
        using var anchor = EcdsaSessionSigner.Generate();
        using var stranger = EcdsaSessionSigner.Generate();
        await using var factory = new AuthTestFactory();
        using WebApplicationFactory<Program> node = NodeKnowing(factory, Published.Of(anchor));
        using HttpClient client = node.CreateClient();

        KgsmIdentity person = Somebody("usr_somebody");
        AuthTestFactory.SetAccountOn(node.Services, person, KgsmTier.Admin);

        string forged = Anchor(stranger).MintAccess(person, KgsmTier.Admin, "sid_1").Token;

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Get("/api/v1/me", forged))).StatusCode);
    }

    [Fact]
    public async Task An_anchor_refresh_token_does_not_authenticate_a_request()
    {
        using var signer = EcdsaSessionSigner.Generate();
        await using var factory = new AuthTestFactory();
        using WebApplicationFactory<Program> node = NodeKnowing(factory, Published.Of(signer));
        using HttpClient client = node.CreateClient();

        KgsmIdentity person = Somebody("usr_somebody");
        AuthTestFactory.SetAccountOn(node.Services, person, KgsmTier.Admin);

        // A 30-day credential presented as a 15-minute one. The access-kind gate is what keeps the
        // short-lived bearer short-lived, and it applies to the cluster's sessions like any other.
        string refresh = Anchor(signer).MintRefresh(person, KgsmTier.Admin, "sid_1").Token;

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Get("/api/v1/me", refresh))).StatusCode);
    }
}
