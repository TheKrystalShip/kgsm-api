using System.Text.Json.Serialization;

namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// One directory entry (file-browser, plan §3.1). <see cref="Kind"/> ∈ <c>{file, dir, symlink, special}</c>.
/// Measured facts only: <see cref="SizeBytes"/>/<see cref="Mtime"/> are <c>null</c> when genuinely
/// unknowable (dirs, special files). <see cref="Editable"/>/<see cref="Lang"/>/<see cref="Reason"/> are
/// PROVISIONAL presentation hints (extension + size) and are omitted when not applicable — a dir has none,
/// an editable file carries <c>editable:true</c>+<c>lang</c>, a blocked entry carries
/// <c>editable:false</c>+<c>reason</c>. The content GET is authoritative for binary/too-large (plan §4.3).
/// </summary>
public sealed record FileEntryDto(
    string Name,
    string Kind,
    long? SizeBytes,
    DateTimeOffset? Mtime,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Editable,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Lang,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason);

/// <summary>A lazy one-directory listing (plan §3.1). <see cref="Truncated"/> ⇒ the entry cap was hit and
/// more exist on disk — an honest signal, never a silent refusal (plan §5).</summary>
public sealed record DirListingDto(string Path, bool Truncated, IReadOnlyList<FileEntryDto> Entries);

/// <summary>A file's raw text + content identity (plan §3.2). <see cref="Content"/> is RAW UTF-8 — no
/// tokenization (the SPA highlights). <see cref="Etag"/> (<c>sha256:…</c>) is for optimistic concurrency
/// on save.</summary>
public sealed record FileContentDto(
    string Path, string Encoding, string Content, long SizeBytes, DateTimeOffset Mtime, string Etag);

/// <summary>Save-an-existing-file request body (plan §3.3). <see cref="Etag"/> is optional but recommended
/// (omit ⇒ last-writer-wins). <see cref="Origin"/> is the caller-declared surface for the audit row
/// (<c>ui|assistant|discord|api</c>, default <c>api</c>) — additive over the plan's <c>{content,etag}</c>.</summary>
public sealed record SaveFileRequest(string? Content, string? Etag, string? Origin);

/// <summary>The result of a successful save (plan §3.3) — the new size/mtime/etag so the editor re-syncs
/// without a re-read.</summary>
public sealed record SaveFileResultDto(string Path, long SizeBytes, DateTimeOffset Mtime, string Etag);
