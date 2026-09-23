using System.Buffers.Binary;
using Oftp4Net.Core.Protocol;

namespace Oftp4Net.Core.Session;

/// <summary>
/// Reads the records of a virtual file from the content stream according to its format (RFC 5024, section 6).
/// Unstructured and text files are one record, fixed files are cut every <c>maxRecordSize</c> octets and the
/// records of a variable file are stored with a 2 octet length in network byte order in front of them, the same
/// way the RFC prescribes for variable files that are signed, compressed or encrypted (section 6.5).
/// </summary>
internal sealed class VirtualFileReader(Stream content, string format, int maxRecordSize)
{
    private readonly byte[] _buffer = new byte[Math.Max(maxRecordSize, 8192)];
    private readonly byte[] _length = new byte[2];

    /// <summary>Records read so far; zero for unstructured and text files, which have no record structure.</summary>
    public long Records { get; private set; }

    /// <summary>
    /// Reads the next piece of the file. <c>EndOfRecord</c> says whether the record ends with it, an empty
    /// result with <c>EndOfFile</c> means there is nothing more to send.
    /// </summary>
    public async ValueTask<(ReadOnlyMemory<byte> Data, bool EndOfRecord, bool EndOfFile)> ReadAsync(
        CancellationToken cancellationToken)
    {
        if (!FileFormats.IsRecordStructured(format))
        {
            // The whole file is a single record, so the end of record flag marks the end of the file.
            var read = await content.ReadAtLeastAsync(_buffer, _buffer.Length, throwOnEndOfStream: false, cancellationToken);
            if (read == 0)
                return (ReadOnlyMemory<byte>.Empty, false, true);

            var atEnd = read < _buffer.Length;
            return (_buffer.AsMemory(0, read), atEnd, false);
        }

        var length = format == FileFormats.Fixed
            ? await ReadFixedAsync(cancellationToken)
            : await ReadVariableAsync(cancellationToken);

        if (length < 0)
            return (ReadOnlyMemory<byte>.Empty, false, true);

        Records++;
        return (_buffer.AsMemory(0, length), true, false);
    }

    /// <summary>Returns the length of the record, or -1 at the end of the file.</summary>
    private async ValueTask<int> ReadFixedAsync(CancellationToken cancellationToken)
    {
        var read = await content.ReadAtLeastAsync(_buffer.AsMemory(0, maxRecordSize), maxRecordSize,
            throwOnEndOfStream: false, cancellationToken);

        if (read == 0)
            return -1;

        if (read < maxRecordSize)
            throw new OftpEncodingException(
                $"The file ends with {read} octets, which is less than the record length {maxRecordSize} of a fixed format file.");

        return read;
    }

    private async ValueTask<int> ReadVariableAsync(CancellationToken cancellationToken)
    {
        var read = await content.ReadAtLeastAsync(_length, 2, throwOnEndOfStream: false, cancellationToken);
        if (read == 0)
            return -1;

        if (read < 2)
            throw new OftpEncodingException("The file ends in the middle of the length of a variable format record.");

        var length = BinaryPrimitives.ReadUInt16BigEndian(_length);
        if (length > maxRecordSize)
            throw new OftpEncodingException(
                $"A record of {length} octets is longer than the maximum record size {maxRecordSize} of the file.");

        if (length > 0 && await content.ReadAtLeastAsync(_buffer.AsMemory(0, length), length,
                throwOnEndOfStream: false, cancellationToken) < length)
            throw new OftpEncodingException("The file ends in the middle of a variable format record.");

        return length;
    }
}

/// <summary>
/// Writes the records of a received virtual file to the destination stream, in the same representation
/// <see cref="VirtualFileReader"/> reads.
/// </summary>
internal sealed class VirtualFileWriter(Stream destination, string format, int maxRecordSize)
{
    private readonly MemoryStream _record = new();
    private readonly byte[] _length = new byte[2];

    /// <summary>Records written so far; zero for unstructured and text files.</summary>
    public long Records { get; private set; }

    /// <summary>
    /// Writes the octets of one data exchange buffer. <paramref name="recordEnds"/> holds the offsets within
    /// <paramref name="data"/> at which a record ended.
    /// </summary>
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, IReadOnlyList<int> recordEnds,
        CancellationToken cancellationToken)
    {
        if (!FileFormats.IsRecordStructured(format))
        {
            // The end of record flag is only the end of file marker here, the octets are written as they come.
            await destination.WriteAsync(data, cancellationToken);
            return;
        }

        var position = 0;
        foreach (var end in recordEnds)
        {
            await AppendAsync(data[position..end], cancellationToken);
            await EndRecordAsync(cancellationToken);
            position = end;
        }

        if (position < data.Length)
            await AppendAsync(data[position..], cancellationToken);
    }

    private ValueTask AppendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
        format == FileFormats.Fixed
            // Fixed records are stored one after another, so they can go to the file right away.
            ? destination.WriteAsync(data, cancellationToken)
            : _record.WriteAsync(data, cancellationToken);

    private async ValueTask EndRecordAsync(CancellationToken cancellationToken)
    {
        Records++;

        if (format == FileFormats.Fixed)
            return;

        if (_record.Length > maxRecordSize && maxRecordSize > 0)
            throw new OftpProtocolException(ReasonCodes.CommandContainedInvalidData,
                $"A record of {_record.Length} octets is longer than the maximum record size {maxRecordSize}.");

        BinaryPrimitives.WriteUInt16BigEndian(_length, (ushort)_record.Length);
        await destination.WriteAsync(_length, cancellationToken);
        await destination.WriteAsync(_record.GetBuffer().AsMemory(0, (int)_record.Length), cancellationToken);
        _record.SetLength(0);
    }
}
