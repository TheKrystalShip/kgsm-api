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

/// <summary>
/// What a recursive name walk matched (<c>GET /servers/{id}/files/find</c>). Each entry's
/// <see cref="FileEntryDto.Name"/> is the path relative to the instance's working directory, not a
/// bare name — the whole point of the walk is that the match was not in the directory asked about.
/// <para>
/// <see cref="Truncated"/> and <see cref="Incomplete"/> are separate and both travel: the first says
/// more matched than were returned and invites a narrower pattern, the second says the walk stopped
/// before it had seen everything. Collapsed into one, "I stopped looking" reads as "that is all there
/// is", which is how a caller concludes a file does not exist when it was never reached.
/// </para>
/// </summary>
public sealed record FileFindDto(
    string Path, bool Truncated, bool Incomplete, IReadOnlyList<FileEntryDto> Matches);

/// <summary>One line a content search matched: the file it is in, its 1-based line number, and the
/// line itself.</summary>
public sealed record FileSearchHitDto(string Path, int Line, string Text);

/// <summary>What a content search found (<c>GET /servers/{id}/files/search</c>). Carries the same two
/// distinct truncation signals as <see cref="FileFindDto"/>, for the same reason.</summary>
public sealed record FileSearchDto(
    string Path, bool Truncated, bool Incomplete, IReadOnlyList<FileSearchHitDto> Hits);
