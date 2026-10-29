using Microsoft.Net.Http.Headers;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.KGSM.Cluster.Identity;

namespace TheKrystalShip.Api.Services.Cluster;

/// <summary>
/// The member a <c>self/*</c> route's caller is: its service token checked fail-closed, and the
/// enabled-member gate applied.
/// </summary>
/// <remarks>
/// These routes are anonymous to the user pipeline and authenticate here, the same way whether or not
/// <c>Api__AuthDisabled</c> is set: a member-to-member call is not a browser session, so the user pipeline
/// must neither gate it nor grant it.
/// </remarks>
public static class ClusterCaller
{
    /// <summary>The caller's member id, or the refusal to answer with: <c>401 invalid_cluster_token</c> or
    /// <c>403 member_disabled</c>.</summary>
    public static async Task<(string? MemberId, int Status, ErrorEnvelope? Refusal)> AuthenticateAsync(
        HttpRequest request, IClusterTokenService tokens, IClusterMemberGate gate)
    {
        string? header = request.Headers[HeaderNames.Authorization];
        const string prefix = "Bearer ";
        string? token = header is not null && header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? header[prefix.Length..].Trim()
            : null;
        if (token is null)
            return (null, StatusCodes.Status401Unauthorized, Envelope("invalid_cluster_token", "missing bearer token"));

        ClusterPrincipal? principal = await tokens.ValidateAsync(token).ConfigureAwait(false);
        if (principal is null)
            return (null, StatusCodes.Status401Unauthorized, Envelope("invalid_cluster_token",
                "invalid, expired, or unsigned cluster service token"));

        if (!await gate.IsEnabledAsync(principal.MemberId).ConfigureAwait(false))
            return (null, StatusCodes.Status403Forbidden, Envelope("member_disabled",
                $"member '{principal.MemberId}' is not an enabled member of this cluster"));

        return (principal.MemberId, StatusCodes.Status200OK, null);
    }

    private static ErrorEnvelope Envelope(string code, string message) => new(new ErrorBody(code, message));
}
