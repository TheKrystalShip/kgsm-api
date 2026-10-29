using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.KGSM.Cluster;
using TheKrystalShip.KGSM.Cluster.Identity;
using TheKrystalShip.KGSM.Cluster.Membership;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM;

namespace TheKrystalShip.Api.Services.Transfers;

/// <summary>
/// What one call to the other node of a transfer came back with.
/// </summary>
/// <param name="Status">The HTTP status, or 0 when the node could not be reached or is not an enabled
/// member here.</param>
/// <param name="Code">The error envelope's code on a non-2xx answer, when it carried one.</param>
/// <param name="Message">The error envelope's message, or what went wrong reaching the node.</param>
public record PeerAnswer(int Status, string? Code, string? Message)
{
    /// <summary>Whether the node answered 2xx.</summary>
    public bool Ok => Status is >= 200 and < 300;

    /// <summary>Whether the node could not be reached at all.</summary>
    public bool Unreachable => Status == 0;
}

/// <summary>A manifest answer from the source: the manifest and the game name it holds for the instance.</summary>
public sealed record ManifestAnswer(int Status, string? Code, string? Message, TransferManifest? Manifest, string? GameName)
    : PeerAnswer(Status, Code, Message);

/// <summary>
/// The member-to-member half of a transfer: every call one node makes to the other, under a cluster
/// service token this node mints.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own HTTP client, with no timeout of its own.</b> A part is streamed for as long as it takes, and
/// tens of gigabytes over a 100 Mb/s link is hours. Every call that is not a part carries a bound of its
/// own, so a node that stops answering a small call is unreachable rather than a hung job.
/// </para>
/// <para>
/// <b>The peer is looked up by member id each call</b>, at the address this node proved it can reach, and
/// only while it is an enabled member here. A peer this node has disabled is unreachable to a transfer as
/// it is to everything else.
/// </para>
/// <para>
/// The manifest crosses as kgsm-lib serializes it (<c>KgsmJsonContext</c>), never re-shaped: it is the
/// engine's record and the target's engine reads it back.
/// </para>
/// </remarks>
public sealed class TransferPeerClient(
    MembersStore members,
    IClusterTokenService tokens,
    IHttpClientFactory httpClientFactory,
    ILogger<TransferPeerClient> logger)
{
    /// <summary>The named client every transfer call goes through. Its timeout is infinite.</summary>
    public const string HttpClientName = "kgsm-api-transfer";

    /// <summary>The bound on a call that makes the source read its instance (the manifest, the lock).</summary>
    public static readonly TimeSpan ManifestTimeout = TimeSpan.FromMinutes(5);

    /// <summary>The bound on every other call that is not a part.</summary>
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);

    private static string Base(string transferId) =>
        "api/v1/members/self/transfers/" + Uri.EscapeDataString(transferId);

    /// <summary>The source's manifest, unlocked.</summary>
    public Task<ManifestAnswer> ManifestAsync(string source, string transferId, string ticket, CancellationToken ct) =>
        ManifestCallAsync(HttpMethod.Get, source, Base(transferId) + "/manifest", ticket, ct);

    /// <summary>Asks the source to take the lock; the manifest as of the lock.</summary>
    public Task<ManifestAnswer> LockAsync(string source, string transferId, string ticket, CancellationToken ct) =>
        ManifestCallAsync(HttpMethod.Post, source, Base(transferId) + "/lock", ticket, ct);

    /// <summary>
    /// Opens one part's stream. On success the caller owns the response and reads its body; otherwise the
    /// answer says why and the response is null.
    /// </summary>
    public async Task<(PeerAnswer Answer, HttpResponseMessage? Response)> OpenPartAsync(
        string source, string transferId, string ticket, string partId, CancellationToken ct)
    {
        if (await UrlOfAsync(source, ct).ConfigureAwait(false) is not { } url)
            return (NotMember(source), null);

        HttpResponseMessage? response = null;
        try
        {
            using HttpRequestMessage request = Request(HttpMethod.Get,
                url + "/" + Base(transferId) + "/parts/" + Uri.EscapeDataString(partId), ticket);
            response = await Client().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
                return (new PeerAnswer((int)response.StatusCode, null, null), response);

            PeerAnswer refused = await RefusalAsync(response, ct).ConfigureAwait(false);
            response.Dispose();
            return (refused, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException
                                   || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            response?.Dispose();
            logger.LogWarning(ex, "transfer {TransferId}: part {PartId} could not be opened on {Member}",
                transferId, partId, source);
            return (new PeerAnswer(0, TransferRefusalCodes.MemberUnreachable, ex.Message), null);
        }
    }

    /// <summary>The SHA-256 of the source's last complete send of a part; null with the answer otherwise.</summary>
    public async Task<(PeerAnswer Answer, string? Sha256)> PartHashAsync(
        string source, string transferId, string ticket, string partId, CancellationToken ct)
    {
        (PeerAnswer answer, string? body) = await CallAsync(HttpMethod.Get, source,
            Base(transferId) + "/parts/" + Uri.EscapeDataString(partId) + "/sha256", ticket, null,
            CallTimeout, ct).ConfigureAwait(false);
        if (!answer.Ok || body is null) return (answer, null);

        TransferPartHash? hash = Read(body, ApiContractsJson.Default.TransferPartHash);
        return (answer, hash?.Sha256);
    }

    /// <summary>Tells the source to release the lock, saying why. Idempotent at the source.</summary>
    public async Task<PeerAnswer> AbortAsync(
        string source, string transferId, string ticket, string reason, string phase, CancellationToken ct)
    {
        string json = JsonSerializer.Serialize(new TransferAbortRequest(reason, phase),
            ApiContractsJson.Default.TransferAbortRequest);
        (PeerAnswer answer, _) = await CallAsync(HttpMethod.Post, source, Base(transferId) + "/abort", ticket,
            json, CallTimeout, ct).ConfigureAwait(false);
        return answer;
    }

    /// <summary>Asks the target how a transfer ended; the outcome on <c>200</c>, null otherwise.</summary>
    public async Task<(PeerAnswer Answer, TransferOutcome? Outcome)> OutcomeAsync(
        string target, string transferId, CancellationToken ct)
    {
        (PeerAnswer answer, string? body) = await CallAsync(HttpMethod.Get, target, Base(transferId), null, null,
            CallTimeout, ct).ConfigureAwait(false);
        if (!answer.Ok || body is null) return (answer, null);

        TransferOutcome? outcome = Read(body, ApiContractsJson.Default.TransferOutcome);
        return outcome is null
            ? (new PeerAnswer(0, TransferRefusalCodes.MemberUnreachable, "the target's answer could not be read"), null)
            : (answer, outcome);
    }

    private async Task<ManifestAnswer> ManifestCallAsync(
        HttpMethod method, string source, string path, string ticket, CancellationToken ct)
    {
        if (await UrlOfAsync(source, ct).ConfigureAwait(false) is not { } url)
        {
            PeerAnswer missing = NotMember(source);
            return new ManifestAnswer(missing.Status, missing.Code, missing.Message, null, null);
        }

        using CancellationTokenSource bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bound.CancelAfter(ManifestTimeout);
        try
        {
            using HttpRequestMessage request = Request(method, url + "/" + path, ticket);
            using HttpResponseMessage response = await Client().SendAsync(request, bound.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                PeerAnswer refused = await RefusalAsync(response, bound.Token).ConfigureAwait(false);
                return new ManifestAnswer(refused.Status, refused.Code, refused.Message, null, null);
            }

            string body = await response.Content.ReadAsStringAsync(bound.Token).ConfigureAwait(false);
            TransferManifest? manifest = Read(body, KgsmJsonContext.Default.TransferManifest);
            if (manifest is null)
            {
                return new ManifestAnswer(0, TransferRefusalCodes.MemberUnreachable,
                    "the source's manifest could not be read", null, null);
            }

            string? gameName = response.Headers.TryGetValues(TransferHeaders.GameName, out IEnumerable<string>? values)
                ? values.FirstOrDefault() is { Length: > 0 } name ? name : null
                : null;
            return new ManifestAnswer((int)response.StatusCode, null, null, manifest, gameName);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException
                                   || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning(ex, "transfer call {Method} {Path} to {Member} failed", method, path, source);
            return new ManifestAnswer(0, TransferRefusalCodes.MemberUnreachable, ex.Message, null, null);
        }
    }

    private async Task<(PeerAnswer Answer, string? Body)> CallAsync(
        HttpMethod method, string member, string path, string? ticket, string? json, TimeSpan timeout,
        CancellationToken ct)
    {
        if (await UrlOfAsync(member, ct).ConfigureAwait(false) is not { } url)
            return (NotMember(member), null);

        using CancellationTokenSource bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bound.CancelAfter(timeout);
        try
        {
            using HttpRequestMessage request = Request(method, url + "/" + path, ticket);
            if (json is not null)
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            using HttpResponseMessage response = await Client().SendAsync(request, bound.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return (await RefusalAsync(response, bound.Token).ConfigureAwait(false), null);

            string body = await response.Content.ReadAsStringAsync(bound.Token).ConfigureAwait(false);
            return (new PeerAnswer((int)response.StatusCode, null, null), body);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException
                                   || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning(ex, "transfer call {Method} {Path} to {Member} failed", method, path, member);
            return (new PeerAnswer(0, TransferRefusalCodes.MemberUnreachable, ex.Message), null);
        }
    }

    private async Task<string?> UrlOfAsync(string memberId, CancellationToken ct)
    {
        MemberRow? row = await members.GetByMemberIdAsync(memberId, ct).ConfigureAwait(false);
        return row is { Enabled: true } && !string.IsNullOrWhiteSpace(row.Url) ? row.Url.TrimEnd('/') : null;
    }

    private HttpRequestMessage Request(HttpMethod method, string url, string? ticket)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.Mint().Token);
        if (ticket is not null)
            request.Headers.TryAddWithoutValidation(TransferHeaders.Ticket, ticket);
        return request;
    }

    private HttpClient Client() => httpClientFactory.CreateClient(HttpClientName);

    private static PeerAnswer NotMember(string memberId) =>
        new(0, TransferRefusalCodes.MemberUnreachable, $"'{memberId}' is not an enabled member of this cluster");

    // A refusal's code and message from the error envelope every member answers with; a body that is not
    // one leaves both to the status.
    private static async Task<PeerAnswer> RefusalAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        ErrorEnvelope? envelope = Read(body, ApiContractsJson.Default.ErrorEnvelope);
        return new PeerAnswer((int)response.StatusCode, envelope?.Error.Code, envelope?.Error.Message ?? body);
    }

    private static T? Read<T>(string body, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) where T : class
    {
        try { return JsonSerializer.Deserialize(body, info); }
        catch (JsonException) { return null; }
    }
}
