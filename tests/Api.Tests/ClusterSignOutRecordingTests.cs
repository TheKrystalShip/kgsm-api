using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;

using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Sessions;
using TheKrystalShip.KGSM.Auth.Cluster;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Who records a sign-out, when the session was not this node's to begin with.
/// </summary>
/// <remarks>
/// <para>
/// A panel signs out at the anchor <em>and</em> at the member it is driving — the first ends the
/// session, the second drops this node's own cache of it. Both used to write <c>auth.signed_out</c>,
/// so one sign-out appeared on the audit page twice, 51ms apart, and the two rows could not be
/// deduplicated because they were honestly from different producers about the same fact.
/// </para>
/// <para>
/// The revoke stays. It is this node's own deny-list entry and takes effect at once rather than when
/// the anchor's broadcast arrives; it is the <em>record</em> that belongs to whoever ended the thing.
/// </para>
/// </remarks>
public sealed class ClusterSignOutRecordingTests
{
    private const string ClusterId = "test-cluster";
    private const string AnchorIssuer = "kgsm";

    private sealed record Published(string? Audience, string? Issuer, IReadOnlyList<SecurityKey> Keys)
        : IClusterSessionKeys
    {
        public static Published Of(EcdsaSessionSigner signer) =>
            new(ClusterId, AnchorIssuer, EcdsaSessionSigner.VerificationKeysFrom(signer.PublicKeys));
    }

    private static WebApplicationFactory<Program> NodeKnowing(
        AuthTestFactory factory, IClusterSessionKeys published) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IClusterSessionKeys>();
            services.AddSingleton(published);
        }));

    private static SessionTokenService Anchor(EcdsaSessionSigner signer) =>
        new(new SessionTokenOptions(
                HostId: ClusterId,
                SigningKey: "",
                AccessLifetime: TimeSpan.FromMinutes(15),
                RefreshLifetime: TimeSpan.FromDays(30),
                Issuer: AnchorIssuer),
            logger: null,
            signer: signer);

    private static KgsmIdentity Somebody(string subject) =>
        new("discord", subject, subject, subject, null, []);

    private static async Task<HttpResponseMessage> SignOutAsync(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/logout");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    /// <summary>Every <c>auth.signed_out</c> line this node has written.</summary>
    private static IReadOnlyList<JsonElement> SignOutLines(IServiceProvider services)
    {
        string directory = services.GetRequiredService<ApiOptions>().EventJournalDir;
        if (!Directory.Exists(directory))
            return [];

        var lines = new List<JsonElement>();
        foreach (string segment in Directory.GetFiles(directory, "*.ndjson"))
        {
            foreach (string line in File.ReadAllLines(segment))
            {
                if (line.Length == 0)
                    continue;

                JsonElement parsed = JsonDocument.Parse(line).RootElement.Clone();
                if (parsed.GetProperty("EventType").GetString() == ApiJournal.LogoutEvent)
                    lines.Add(parsed);
            }
        }

        return lines;
    }

    [Fact]
    public async Task Signing_out_a_session_the_anchor_minted_records_nothing_here()
    {
        using var signer = EcdsaSessionSigner.Generate();
        await using var factory = new AuthTestFactory();
        using WebApplicationFactory<Program> node = NodeKnowing(factory, Published.Of(signer));
        using HttpClient client = node.CreateClient();

        KgsmIdentity person = Somebody("usr_replicated");
        AuthTestFactory.SetAccountOn(node.Services, person, KgsmTier.Operator);

        string token = Anchor(signer).MintAccess(person, KgsmTier.Operator, "sid_from_anchor").Token;

        Assert.Equal(HttpStatusCode.NoContent, (await SignOutAsync(client, token)).StatusCode);

        // The anchor ended it and recorded that it did. A line here would be the same sign-out on the
        // audit page twice, from two producers, with nothing able to tell they are one fact.
        Assert.Empty(SignOutLines(node.Services));

        // The revoke still happened: this node's own deny-list entry, taking effect at once rather
        // than when the anchor's broadcast arrives.
        Assert.True(await node.Services.GetRequiredService<SessionStore>()
            .IsRevokedAsync("sid_from_anchor", default));
    }

    [Fact]
    public async Task Signing_out_a_session_this_node_minted_is_still_recorded_here()
    {
        await using var factory = new AuthTestFactory();
        using HttpClient client = factory.CreateClient();

        // No anchor in sight — a standalone host, which is what most installs are and the case this
        // node has always answered for. Nothing else records its sign-outs, so it must.
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await SignOutAsync(client, factory.AccessToken(KgsmTier.Operator))).StatusCode);

        Assert.Single(SignOutLines(factory.Services));
    }
}
