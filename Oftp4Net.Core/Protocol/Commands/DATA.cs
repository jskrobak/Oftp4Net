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

    private const byte CompressedFlag = 0x40;
    private const byte CountMask = 0x3F;

    public override char Indicator => Id;

    /// <summary>Encoded subrecords (the exchange buffer without the indicator).</summary>
    public byte[] Subrecords { get; init; } = [];

    internal override void Write(CommandWriter writer) => writer.Raw(Subrecords);

    /// <summary>
    /// Splits the payload into subrecords. Buffer compression is not used when sending.
    /// </summary>
    public static DATA FromPayload(ReadOnlySpan<byte> payload)
    {
        var subrecordCount = (payload.Length + MaxSubrecordLength - 1) / MaxSubrecordLength;
        var buffer = new byte[payload.Length + subrecordCount];

        var position = 0;
        while (!payload.IsEmpty)
        {
            var chunk = payload[..Math.Min(MaxSubrecordLength, payload.Length)];
            buffer[position++] = (byte)chunk.Length;
            chunk.CopyTo(buffer.AsSpan(position));
            position += chunk.Length;
            payload = payload[chunk.Length..];
        }

        return new DATA { Subrecords = buffer };
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

    /// <summary>Decodes the subrecords into the payload octets, expanding compressed subrecords.</summary>
    public int DecodeTo(Stream destination)
    {
        var total = 0;
        var position = 0;
        var data = Subrecords;
        while (position < data.Length)
        {
            var header = data[position++];
            var count = header & CountMask;

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
        }

        return total;
    }

    public override string ToString() => $"DATA <{Subrecords.Length} bytes>";
}
