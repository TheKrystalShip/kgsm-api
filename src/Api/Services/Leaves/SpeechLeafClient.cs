using TheKrystalShip.KGSM.Speech;

namespace TheKrystalShip.Api.Services.Leaves;

/// <summary>
/// The kgsm-speech leaf, as this API reaches it — through the leaf's own published client
/// (<c>TheKrystalShip.KGSM.Speech</c>), never by hand-rolling its wire format.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read-only, and never polled.</b> The daemon is socket-activated and idle-exits to give back the
/// ~1.6GB its models cost, so <em>connecting is what starts it</em>. This client is used on a page
/// view and nowhere else: there is deliberately no <see cref="LeafHealthMonitor"/> entry for speech,
/// because a periodic probe would keep a process alive purely to be asked whether it is alive.
/// </para>
/// <para>
/// <b>The socket file is the provisioning check.</b> systemd binds it whether or not the daemon runs,
/// so its presence answers "is this leaf installed here" without starting anything — which is what
/// lets the API 404 a host with no speech leaf rather than reporting one that is permanently down.
/// </para>
/// <para>
/// <b>Which is why speech carries no Link on the Services board</b> and is absent from
/// <see cref="ProvisionableLeaf"/>. That axis is a stored connection an admin can turn off, and there is
/// nothing here for one to arm: this client runs on a page view, holds no poll and feeds no data flow, and
/// the paths that actually use the engine — the assistant service, the bot, a browser recording a voice
/// note — reach the leaf directly. A toggle would stop none of them while looking like the switch that
/// does. A stored row could also contradict the file, and the file is the measurement.
/// </para>
/// <para>
/// Honesty: an unreachable or malformed answer is <c>null</c>. The controller turns that into a 503
/// saying the leaf would not answer — never into an empty status, which would render as a healthy
/// engine that has simply done nothing.
/// </para>
/// </remarks>
public sealed class SpeechLeafClient : IDisposable
{
    /// <summary>
    /// How long to wait for the daemon to answer.
    /// </summary>
    /// <remarks>
    /// A status is composed from what is already in hand and loads nothing, so the only thing this has
    /// to absorb is systemd starting the process on the connection. It must never be long enough to
    /// stall the page it is drawn on.
    /// </remarks>
    private static readonly TimeSpan AnswerWithin = TimeSpan.FromSeconds(5);

    private readonly SpeechClient _client;
    private readonly string _socketPath;

    public SpeechLeafClient(ApiOptions options, ILogger<SpeechLeafClient> logger)
    {
        _socketPath = options.SpeechSocketPath;
        _client = new SpeechClient(_socketPath, logger);
    }

    /// <summary>Whether this host has a speech leaf at all — asked without starting anything.</summary>
    public bool IsProvisioned => _client.IsProvisioned;

    /// <summary>Where this client is pointed, for a surface that has to say why it found nothing.</summary>
    public string SocketPath => _socketPath;

    /// <summary>
    /// What the daemon is doing right now, or null when it would not answer.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Asking starts the daemon</b> (it loads no model, so what starts is a small process), and
    /// deliberately does not push its idle deadline out — the leaf excludes this message from what
    /// counts as being used, so watching it never keeps it resident.
    /// </remarks>
    public async Task<SpeechStatus?> GetStatusAsync(CancellationToken ct = default)
    {
        using var timed = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timed.CancelAfter(AnswerWithin);

        try
        {
            return await _client.StatusAsync(timed.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    public void Dispose() => _client.Dispose();
}
