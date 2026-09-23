using System.Buffers;

namespace Oftp4Net.Core.Protocol;

/// <summary>
/// Frames OFTP exchange buffers on a byte stream using the Stream Transmission Header (RFC 5024, section 10.2).
/// </summary>
/// <remarks>
/// Each Stream Transmission Buffer starts with a 4 octet header: version (4 bits, always 1), flags (4 bits, always 0)
/// and a 24 bit big-endian length that includes the header itself.
/// </remarks>
public sealed class OftpTransport(Stream stream) : IAsyncDisposable
{
    public const int HeaderLength = 4;
    public const int MaxExchangeBufferSize = 99999;
    private const byte Version = 0x10;

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly byte[] _readHeader = new byte[HeaderLength];

    public Stream Stream => stream;

    public static byte[] CreateHeader(int exchangeBufferLength)
    {
        if (exchangeBufferLength is < 1 or > MaxExchangeBufferSize)
            throw new ArgumentOutOfRangeException(nameof(exchangeBufferLength),
                $"Exchange buffer length must be between 1 and {MaxExchangeBufferSize}.");

        var total = exchangeBufferLength + HeaderLength;
        return [Version, (byte)(total >> 16), (byte)(total >> 8), (byte)total];
    }

    public async Task WriteAsync(byte[] exchangeBuffer, CancellationToken cancellationToken)
    {
        var header = CreateHeader(exchangeBuffer.Length);

        // Header and buffer go out in a single write: two writes would cost two TLS records and, with Nagle
        // disabled, two TCP segments for every exchange buffer.
        var frame = ArrayPool<byte>.Shared.Rent(HeaderLength + exchangeBuffer.Length);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            header.CopyTo(frame, 0);
            exchangeBuffer.CopyTo(frame, HeaderLength);
            await stream.WriteAsync(frame.AsMemory(0, HeaderLength + exchangeBuffer.Length), cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        catch (IOException ex)
        {
            throw new OftpConnectionClosedException("Connection closed while writing.", ex);
        }
        finally
        {
            _writeLock.Release();
            ArrayPool<byte>.Shared.Return(frame);
        }
    }

    /// <summary>
    /// Reads the next exchange buffer (without the Stream Transmission Header).
    /// </summary>
    public async Task<byte[]> ReadAsync(CancellationToken cancellationToken)
    {
        // Only one read is in progress at a time, so the header buffer is reused.
        var header = _readHeader;
        await ReadExactlyAsync(header, cancellationToken);

        if ((header[0] & 0xF0) != Version)
            throw new OftpProtocolException(ReasonCodes.ProtocolViolation,
                $"Unsupported Stream Transmission Header version 0x{header[0]:X2}.");

        var total = (header[1] << 16) | (header[2] << 8) | header[3];
        var length = total - HeaderLength;
        if (length is < 1 or > MaxExchangeBufferSize)
            throw new OftpProtocolException(ReasonCodes.ProtocolViolation,
                $"Invalid Stream Transmission Buffer length {total}.");

        var buffer = new byte[length];
        await ReadExactlyAsync(buffer, cancellationToken);
        return buffer;
    }

    private async Task ReadExactlyAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        try
        {
            await stream.ReadExactlyAsync(buffer, cancellationToken);
        }
        catch (EndOfStreamException ex)
        {
            throw new OftpConnectionClosedException("Connection closed by peer.", ex);
        }
        catch (IOException ex)
        {
            throw new OftpConnectionClosedException("Connection closed while reading.", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await stream.DisposeAsync();
        _writeLock.Dispose();
    }
}
