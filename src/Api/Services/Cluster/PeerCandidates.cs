using System.Text.Json;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Json;

namespace TheKrystalShip.Api.Services.Cluster;

/// <summary>
/// Reads and writes <see cref="Data.PeerEntity.Candidates"/> — the JSON array of addresses a peer says it
/// answers at (<c>PLAN-peers.md</c> §2 #13a) — and picks which of them this node calls.
/// </summary>
public static class PeerCandidates
{
    private static readonly JsonSerializerOptions Options = Build();

    private static JsonSerializerOptions Build()
    {
        var options = new JsonSerializerOptions();
        ApiJson.Configure(options);
        return options;
    }

    /// <summary>Decode a stored candidate list. A blank or unparseable column reads as empty — a roster row
    /// written before the peer offered any address is a gap, not a failure.</summary>
    public static IReadOnlyList<NodeCandidate> Decode(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<NodeCandidate>>(stored, Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Encode a candidate list for storage, dropping anything that is not an absolute http(s)
    /// address and de-duplicating by normalised URL while keeping the offered order.</summary>
    public static string Encode(IEnumerable<NodeCandidate>? candidates)
    {
        if (candidates is null) return "";
        var seen = new Dictionary<string, NodeCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (NodeCandidate candidate in candidates)
        {
            string? url = SelfIdentityStore.Normalize(candidate.Url);
            if (url is null || seen.ContainsKey(url)) continue;
            seen[url] = new NodeCandidate(url, candidate.Client);
        }
        return seen.Count == 0 ? "" : JsonSerializer.Serialize(seen.Values.ToList(), Options);
    }

    /// <summary>
    /// Merge a freshly-offered list into what is already stored: the offer leads (a node is the authority on
    /// its own addresses) and anything previously known that the offer omits is kept behind it, so an address
    /// this node has proven works is not dropped because one gossip round arrived with a shorter list.
    /// </summary>
    public static string Merge(string? stored, IEnumerable<NodeCandidate>? offered) =>
        Encode([.. offered ?? [], .. Decode(stored)]);

    /// <summary>The address to call a peer on: the first candidate a browser can also use, else the first of
    /// any kind, else empty. Node-to-node accepts either — a node-only address is still an address — while
    /// the roster's client URL is filtered separately by <see cref="ClientUrl"/>.</summary>
    public static string Best(IReadOnlyList<NodeCandidate> candidates) =>
        candidates.Count == 0
            ? ""
            : (candidates.FirstOrDefault(c => c.Client) ?? candidates[0]).Url;

    /// <summary>The address the SPA is given for a peer: the first candidate a browser can use. Empty when
    /// the peer offers none — the roster reports the gap rather than handing the browser a node-only
    /// address it cannot reach.</summary>
    public static string ClientUrl(IReadOnlyList<NodeCandidate> candidates) =>
        candidates.FirstOrDefault(c => c.Client)?.Url ?? "";
}
