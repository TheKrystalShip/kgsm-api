using System.Text.Json.Serialization;

namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// What the host's speech engine is doing — <c>GET /hosts/{id}/services/speech/status</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Resting"/> is the field everything else hangs off.</b> The leaf is socket-activated
/// and idle-exits to give back the ~1.6GB its models cost, so an inactive unit is its normal resting
/// state and NOT a fault. When it is resting this API deliberately does not connect — connecting is
/// what starts the daemon, and starting one to draw a page would defeat the exit it just made. The
/// live half of this record is then null, and what remains is what can be known without asking: the
/// unit's state, the model files on disk, and the voice the configuration names.
/// </para>
/// <para>
/// <b>Nothing here is recomputed.</b> Every live figure is the leaf's own measurement relayed across:
/// which runtime each half actually loaded on, what it has heard and said, when it unloads. A field
/// that could not be measured is null — never the configured value standing in for the measured one,
/// which is the exact substitution that makes a CPU fallback invisible.
/// </para>
/// </remarks>
/// <param name="Resting">The daemon is not running and was deliberately not started to answer this.</param>
/// <param name="State">The unit's systemd state as read (<c>active</c>, <c>inactive</c>, <c>unknown</c>).</param>
/// <param name="StartedAt">When the running daemon process started — what every counter is counted since.</param>
/// <param name="Loaded">Whether the models are in memory. False on a running daemon nobody has needed yet.</param>
/// <param name="LoadedAt">When they were loaded.</param>
/// <param name="LoadMs">How long loading took.</param>
/// <param name="IdleMinutes">Minutes of quiet before the daemon unloads and exits. Zero stays loaded.</param>
/// <param name="LastAskedAt">When something was last asked of it. Reading this status is not asking.</param>
/// <param name="UnloadsAt">When the models unload if nothing else is asked. Null when it stays loaded.</param>
/// <param name="Surfaces">The processes connected right now, by name, from the kernel's own credentials.</param>
/// <param name="Voice">Which voice this host speaks in, and whether that is the configured one.</param>
/// <param name="Hearing">Recognition. Null while resting.</param>
/// <param name="Speaking">Synthesis. Null while resting.</param>
/// <param name="Models">The model files the leaf's configuration points at, measured on disk.</param>
public sealed record SpeechEngine(
    bool Resting,
    string State,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? StartedAt,
    bool Loaded,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? LoadedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? LoadMs,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? IdleMinutes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? LastAskedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? UnloadsAt,
    IReadOnlyList<string> Surfaces,
    SpeechVoice Voice,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SpeechLaneReport? Hearing,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SpeechLaneReport? Speaking,
    IReadOnlyList<SpeechModelFile> Models);

/// <summary>
/// The voice this host speaks in.
/// </summary>
/// <remarks>
/// <see cref="Speaking"/> can be changed on the running daemon, for every surface at once, and is
/// deliberately never written back — so <see cref="Configured"/> is what the next process will use.
/// This is the only surface on which the two are visible together.
/// </remarks>
/// <param name="Speaking">What it is saying things in right now. Empty while resting.</param>
/// <param name="Configured">What its configuration names.</param>
/// <param name="Overridden">The two disagree: somebody changed it on the running daemon.</param>
/// <param name="Installed">How many voices are installed. Null while resting — nothing counted them.</param>
public sealed record SpeechVoice(
    string Speaking,
    string Configured,
    bool Overridden,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Installed);

/// <summary>
/// One half of the engine — hearing or speaking — and how it has been getting on.
/// </summary>
/// <remarks>
/// The two are reported separately because either can be unavailable while the other works, and
/// because they can end up on different processors: whisper and Kokoro each fall back to the CPU on
/// their own terms, and a host with no cuDNN hears on the card and speaks on the processor.
/// </remarks>
/// <param name="Available">Whether this half can do anything at all.</param>
/// <param name="Detail">What it is doing, or the reason it can do nothing.</param>
/// <param name="Model">The model file it loaded.</param>
/// <param name="Runtime">What it ACTUALLY loaded on — <c>gpu</c>, <c>cpu</c>, or <c>unknown</c>. Not the setting.</param>
/// <param name="Busy">A pass is running right now.</param>
/// <param name="Waiting">How many requests are queued behind it — this engine runs one pass at a time.</param>
/// <param name="Done">Passes that ran and produced an answer.</param>
/// <param name="Rejected">Requests turned away because a pass was running and the caller would not wait.</param>
/// <param name="Failed">Passes that were attempted and threw.</param>
/// <param name="AudioSeconds">Seconds of audio read, or produced.</param>
/// <param name="Characters">Characters said. Zero on the recognition side, which is paid per utterance.</param>
/// <param name="LastMs">How long the last pass took.</param>
/// <param name="MeanMs">The mean over the passes the leaf still remembers.</param>
/// <param name="P95Ms">The 95th percentile over those — what a slow one costs.</param>
/// <param name="RealtimeFactor">Seconds of audio per second of work. Null until a pass has been timed.</param>
/// <param name="LastAt">When the last pass finished.</param>
/// <param name="LastOutcome">How it went: <c>done</c>, <c>busy</c>, <c>unavailable</c> or <c>failed</c>.</param>
public sealed record SpeechLaneReport(
    bool Available,
    string Detail,
    string Model,
    string Runtime,
    bool Busy,
    int Waiting,
    long Done,
    long Rejected,
    long Failed,
    double AudioSeconds,
    long Characters,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? LastMs,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MeanMs,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? P95Ms,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? RealtimeFactor,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? LastAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LastOutcome);

/// <summary>
/// One model file the leaf's configuration points at, measured on disk by this API.
/// </summary>
/// <remarks>
/// Readable whether or not the daemon is running, which is what lets a resting leaf still report that
/// its 813MB of models are there — the question somebody actually has when nothing will speak.
/// <see cref="Bytes"/> is null when the path could not be measured, which is not the same as
/// <see cref="Present"/> being false.
/// </remarks>
/// <param name="Kind"><c>recognition</c> or <c>synthesis</c>.</param>
/// <param name="Name">The file's name.</param>
/// <param name="Path">Where the configuration says it is.</param>
/// <param name="Bytes">Its size on disk.</param>
/// <param name="Present">Whether it is there at all.</param>
public sealed record SpeechModelFile(
    string Kind,
    string Name,
    string Path,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Bytes,
    bool Present);
