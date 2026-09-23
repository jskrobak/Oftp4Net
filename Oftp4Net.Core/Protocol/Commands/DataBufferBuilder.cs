namespace Oftp4Net.Core.Protocol.Commands;

/// <summary>
/// Packs virtual file records into data exchange buffers (RFC 5024, section 7): a record is split into subrecords
/// of at most 63 octets, the last subrecord of a record carries the End of Record flag, and a record may continue
/// in the following buffer. Unstructured and text files are one single record, so the flag marks the end of the
/// file for them.
/// </summary>
internal sealed class DataBufferBuilder(int exchangeBufferSize, bool compress)
{
    private const int MinCompressibleRun = 3;
    private const byte EndOfRecordFlag = 0x80;
    private const byte CompressedFlag = 0x40;

    private readonly byte[] _buffer = new byte[exchangeBufferSize];
    private int _position = 1;

    public bool IsEmpty => _position <= 1;

    /// <summary>Octets of the virtual file that are already in the buffer.</summary>
    public int PayloadLength { get; private set; }

    /// <summary>
    /// Appends as much of <paramref name="record"/> as fits into the buffer and returns how many octets were
    /// taken. <paramref name="completed"/> says whether the whole record is in the buffer, including its End of
    /// Record flag; when it is not, the buffer is full and the rest of the record belongs into the next one.
    /// </summary>
    public int Append(ReadOnlySpan<byte> record, bool endOfRecord, out bool completed)
    {
        var taken = 0;

        while (taken < record.Length && Remaining >= 2)
        {
            var rest = record[taken..];
            var run = compress ? RunLength(rest) : 0;

            if (run >= MinCompressibleRun)
            {
                // A compressed subrecord is the header and the octet that is repeated.
                Write((byte)(CompressedFlag | run), endOfRecord && run == rest.Length);
                _buffer[_position++] = rest[0];
                taken += run;
                PayloadLength += run;
                continue;
            }

            var length = Math.Min(Math.Min(compress ? LiteralLength(rest) : DATA.MaxSubrecordLength, rest.Length),
                Remaining - 1);

            Write((byte)length, endOfRecord && length == rest.Length);
            rest[..length].CopyTo(_buffer.AsSpan(_position));
            _position += length;
            taken += length;
            PayloadLength += length;
        }

        if (taken < record.Length)
        {
            completed = false;
            return taken;
        }

        // An empty record is only its End of Record flag.
        if (endOfRecord && record.Length == 0)
        {
            if (Remaining < 1)
            {
                completed = false;
                return 0;
            }

            _buffer[_position++] = EndOfRecordFlag;
        }

        completed = true;
        return taken;
    }

    private void Write(byte header, bool endOfRecord) =>
        _buffer[_position++] = (byte)(endOfRecord ? header | EndOfRecordFlag : header);

    public DATA ToData()
    {
        var data = new DATA { Subrecords = _buffer[1.._position] };
        _position = 1;
        PayloadLength = 0;
        return data;
    }

    private int Remaining => _buffer.Length - _position;

    private static int RunLength(ReadOnlySpan<byte> payload)
    {
        var value = payload[0];
        var length = 1;
        while (length < payload.Length && length < DATA.MaxSubrecordLength && payload[length] == value)
            length++;

        return length;
    }

    private static int LiteralLength(ReadOnlySpan<byte> payload)
    {
        var limit = Math.Min(DATA.MaxSubrecordLength, payload.Length);
        for (var length = 1; length < limit; length++)
        {
            if (RunLength(payload[length..]) >= MinCompressibleRun)
                return length;
        }

        return limit;
    }
}
