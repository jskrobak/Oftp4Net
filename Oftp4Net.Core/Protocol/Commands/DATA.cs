namespace Oftp4Net.Core.Protocol.Commands;

/// <summary>
/// Data exchange buffer: a sequence of subrecords, each preceded by a one octet header (RFC 5024, section 7.3).
/// </summary>
/// <remarks>
/// Subrecord header: bit 7 = end of record, bit 6 = compressed, bits 0-5 = count (max 63).
/// A compressed subrecord carries a single octet that is to be repeated <c>count</c> times.
/// </remarks>
public sealed class DATA : OftpCommand
{
    public const char Id = 'D';
    public const int MaxSubrecordLength = 63;

    private const byte EndOfRecordFlag = 0x80;
    private const byte CompressedFlag = 0x40;
    private const byte CountMask = 0x3F;

    public override char Indicator => Id;

    /// <summary>Encoded subrecords (the exchange buffer without the indicator).</summary>
    public byte[] Subrecords { get; init; } = [];

    internal override void Write(CommandWriter writer) => writer.Raw(Subrecords);

    /// <summary>
    /// A run of equal octets is worth compressing from this length on: it costs two octets on the wire
    /// (header and value) instead of one octet per repetition.
    /// </summary>
    private const int MinCompressibleRun = 3;

    /// <summary>
    /// Splits the payload into subrecords, optionally using ODETTE-FTP buffer compression, which replaces a run
    /// of equal octets by a single one (SSIDCMPR, only when both sides agreed on it).
    /// </summary>
    public static DATA FromPayload(ReadOnlySpan<byte> payload, bool compress = false, bool endOfRecord = false)
    {
        var subrecordCount = (payload.Length + MaxSubrecordLength - 1) / MaxSubrecordLength;
        var buffer = new byte[payload.Length + subrecordCount];

        var position = 0;
        while (!payload.IsEmpty)
        {
            var run = compress ? RunLength(payload) : 0;
            if (run >= MinCompressibleRun)
            {
                buffer[position++] = (byte)((endOfRecord && run == payload.Length ? EndOfRecordFlag : 0) | CompressedFlag | run);
                buffer[position++] = payload[0];
                payload = payload[run..];
                continue;
            }

            // Literal octets up to the next run worth compressing.
            var length = compress ? LiteralLength(payload) : Math.Min(MaxSubrecordLength, payload.Length);
            buffer[position++] = (byte)((endOfRecord && length == payload.Length ? EndOfRecordFlag : 0) | length);
            payload[..length].CopyTo(buffer.AsSpan(position));
            position += length;
            payload = payload[length..];
        }

        return new DATA { Subrecords = position == buffer.Length ? buffer : buffer[..position] };
    }

    /// <summary>Number of equal octets at the start of <paramref name="payload"/>, at most a subrecord.</summary>
    private static int RunLength(ReadOnlySpan<byte> payload)
    {
        var value = payload[0];
        var length = 1;
        while (length < payload.Length && length < MaxSubrecordLength && payload[length] == value)
            length++;

        return length;
    }

    /// <summary>Number of octets to send uncompressed, i.e. up to the next run worth compressing.</summary>
    private static int LiteralLength(ReadOnlySpan<byte> payload)
    {
        var limit = Math.Min(MaxSubrecordLength, payload.Length);
        for (var length = 1; length < limit; length++)
        {
            if (RunLength(payload[length..]) >= MinCompressibleRun)
                return length;
        }

        return limit;
    }

    /// <summary>
    /// Maximum number of payload octets that fit into a single data exchange buffer of the given size.
    /// </summary>
    public static int MaxPayloadLength(int exchangeBufferSize)
    {
        var available = exchangeBufferSize - 1;
        var full = available / (MaxSubrecordLength + 1);
        var remainder = available % (MaxSubrecordLength + 1);
        return full * MaxSubrecordLength + Math.Max(0, remainder - 1);
    }

    internal static DATA Read(byte[] buffer) => new() { Subrecords = buffer[1..] };

    /// <summary>
    /// Decodes the subrecords into the payload octets, expanding compressed subrecords. Offsets at which a record
    /// ends (the End of Record flag, RFC 5024, section 7.2) are added to <paramref name="recordEnds"/>, counted
    /// from the beginning of this buffer.
    /// </summary>
    public int DecodeTo(Stream destination, List<int>? recordEnds = null)
    {
        var total = 0;
        var position = 0;
        var data = Subrecords;
        while (position < data.Length)
        {
            var header = data[position++];
            var count = header & CountMask;
            var endOfRecord = (header & EndOfRecordFlag) != 0;

            if ((header & CompressedFlag) != 0)
            {
                if (position >= data.Length)
                    throw new OftpProtocolException(ReasonCodes.CommandContainedInvalidData, "Truncated compressed subrecord.");

                var value = data[position++];
                for (var i = 0; i < count; i++)
                    destination.WriteByte(value);
            }
            else
            {
                if (position + count > data.Length)
                    throw new OftpProtocolException(ReasonCodes.CommandContainedInvalidData, "Truncated subrecord.");

                destination.Write(data, position, count);
                position += count;
            }

            total += count;
            if (endOfRecord)
                recordEnds?.Add(total);
        }

        return total;
    }

    public override string ToString() => $"DATA <{Subrecords.Length} bytes>";
}
