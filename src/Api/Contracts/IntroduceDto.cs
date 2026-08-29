namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// One address a node answers at (<c>PLAN-peers.md §2</c> #13a). Reachability is a property of a
/// <em>pair</em>, not of a node, so a node offers every address it knows of itself and each peer pins the
/// one that answers for it.
/// </summary>
/// <param name="Url">An absolute <c>http(s)</c> address, no trailing slash.</param>
/// <param name="Client">Whether a browser can use this address. The two reflection sources are
/// browser-reachable by construction — an operator pastes a URL into a panel, and an observed host IS a
/// browser that arrived — so both carry <see langword="true"/>. <c>Api__ClusterGossipUrl</c> and a
/// peer-observed source address contribute node-only candidates.</param>
public sealed record NodeCandidate(string Url, bool Client);

/// <summary>
/// Everything one node needs to know about another (<c>PLAN-peers.md</c> P0.6) — the payload of the
/// symmetric introduce exchange, and the body of <c>GET /peers/identity</c>'s answer.
/// </summary>
public sealed record NodeCard(
    string NodeId,
    string ApiVersion,
    string Build,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<NodeCandidate> Candidates,
    long Incarnation);

/// <summary>
/// An address one node reports back to the other: "this is where I reached you."
/// </summary>
/// <param name="Url">The absolute address.</param>
/// <param name="Provenance"><c>operator</c> — a human pasted this URL into a panel and it answered, the
/// strongest address statement in the system. <c>observed</c> — the source address the receiver saw the
/// request arrive from, a hint that seeds a candidate and that nothing depends on.</param>
public sealed record ReflectedAddress(string Url, string Provenance);

/// <summary>
/// The symmetric join exchange (<c>PLAN-peers.md §2</c> #6, §7): <c>POST /peers/introduce</c> sends this
/// record and answers with the same one, the push-pull idiom <see cref="SyncRequest"/> already uses. Both
/// sides validate the other's <see cref="Self"/> with the same predicate and record the mirror of what the
/// other records, so adding B from A leaves the identical cluster state as adding A from B.
/// </summary>
/// <param name="Self">The sender's own node card.</param>
/// <param name="YouAre">Where the sender reached the receiver. Null when the sender has no address to
/// report — never a fabricated one.</param>
/// <param name="PanelOrigins">Browser origins this node has seen an admin sign in from, so a panel served
/// from somewhere that is not a node reaches every node without a per-node allowlist.</param>
public sealed record IntroduceExchange(
    NodeCard Self,
    ReflectedAddress? YouAre,
    IReadOnlyList<string> PanelOrigins);
