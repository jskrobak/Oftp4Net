using System.Buffers;

namespace Oftp4Net.Services.Encodings;

/// <summary>
/// Wraps a stream and maps every octet through a 256 entry translation table: octets read from the inner stream
/// are translated before they are returned, octets written are translated before they reach the inner stream.
/// The mapping is one to one, so the length and the position of the stream are those of the inner stream.
/// </summary>
public sealed class ByteTranslatingStream : Stream
{
    private readonly Stream _inner;
    private readonly byte[] _table;

    /// <param name="table">Translation table with one target octet for each of the 256 source octets.</param>
    public ByteTranslatingStream(Stream inner, byte[] table)
    {
        if (table.Length != 256)
            throw new ArgumentException("A translation table has 256 entries.", nameof(table));

        _inner = inner;
        _table = table;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanWrite => _inner.CanWrite;
    public override bool CanSeek => _inner.CanSeek;
    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var length = _inner.Read(buffer);
        Translate(buffer[..length]);
        return length;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var length = await _inner.ReadAsync(buffer, cancellationToken);
        Translate(buffer.Span[..length]);
        return length;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        var translated = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            Translate(buffer, translated);
            _inner.Write(translated.AsSpan(0, buffer.Length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(translated);
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var translated = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            Translate(buffer.Span, translated);
            await _inner.WriteAsync(translated.AsMemory(0, buffer.Length), cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(translated);
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private void Translate(Span<byte> buffer)
    {
        for (var i = 0; i < buffer.Length; i++)
            buffer[i] = _table[buffer[i]];
    }

    private void Translate(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        for (var i = 0; i < source.Length; i++)
            destination[i] = _table[source[i]];
    }

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

    public override void SetLength(long value) => _inner.SetLength(value);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _inner.Dispose();
    }

    public override ValueTask DisposeAsync() => _inner.DisposeAsync();
}
