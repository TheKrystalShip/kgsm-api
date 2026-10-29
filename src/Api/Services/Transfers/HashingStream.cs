using System.Security.Cryptography;

namespace TheKrystalShip.Api.Services.Transfers;

/// <summary>
/// A pass-through stream that hashes and counts the bytes crossing it, in one direction: what is written
/// to the source's response, or what is read from the target's request.
/// </summary>
/// <remarks>
/// Both ends of a part hash what crossed the wire rather than what the engine made of it, so the compare
/// says whether the bytes arrived intact. Nothing is buffered beyond the copy's own chunk.
/// </remarks>
public sealed class HashingStream(Stream inner, bool write, Action<long>? progress = null) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _bytes;

    /// <summary>The bytes that have crossed so far.</summary>
    public long Bytes => _bytes;

    /// <summary>Lower-case hex SHA-256 of everything that crossed. Call once the stream is done.</summary>
    public string Hex() => Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();

    /// <summary>Reads what is left of the inner stream through the hash, so it covers everything sent.</summary>
    public async Task DrainAsync(CancellationToken ct)
    {
        byte[] buffer = new byte[81920];
        while (await ReadAsync(buffer, ct).ConfigureAwait(false) > 0)
        {
        }
    }

    public override bool CanRead => !write;
    public override bool CanWrite => write;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _bytes; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = inner.Read(buffer, offset, count);
        Account(buffer.AsSpan(offset, read));
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Account(buffer.Span[..read]);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count)
    {
        inner.Write(buffer, offset, count);
        Account(buffer.AsSpan(offset, count));
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        Account(buffer.Span);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
        if (write) inner.Flush();
    }

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        write ? inner.FlushAsync(cancellationToken) : Task.CompletedTask;

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    private void Account(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0) return;
        _hash.AppendData(bytes);
        _bytes += bytes.Length;
        progress?.Invoke(_bytes);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _hash.Dispose();
        base.Dispose(disposing);
    }
}
