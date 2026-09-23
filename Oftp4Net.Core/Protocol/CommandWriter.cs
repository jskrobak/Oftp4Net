using System.Globalization;
using System.Text;

namespace Oftp4Net.Core.Protocol;

/// <summary>
/// Builds an OFTP exchange buffer (command indicator followed by fixed-width fields) as defined in RFC 5024, section 5.
/// </summary>
internal sealed class CommandWriter
{
    private readonly MemoryStream _stream = new();

    public CommandWriter(char indicator, int level = ProtocolLevels.Oftp2)
    {
        Level = level;
        _stream.WriteByte((byte)indicator);
    }

    /// <summary>Negotiated protocol release level the command is written for.</summary>
    public int Level { get; }

    /// <summary>Alphanumeric field X(n): ASCII, left justified, padded with spaces.</summary>
    public CommandWriter Alpha(string? value, int length)
    {
        value ??= string.Empty;
        if (value.Length > length)
            throw new OftpEncodingException($"Value '{value}' exceeds field length {length}.");

        foreach (var ch in value)
        {
            if (ch > 0x7F)
                throw new OftpEncodingException($"Value '{value}' contains a non-ASCII character.");
        }

        _stream.Write(Encoding.ASCII.GetBytes(value.PadRight(length)));
        return this;
    }

    /// <summary>Numeric field 9(n): ASCII digits, right justified, padded with zeros.</summary>
    public CommandWriter Numeric(long value, int length)
    {
        if (value < 0)
            throw new OftpEncodingException($"Numeric value {value} must not be negative.");

        var text = value.ToString(CultureInfo.InvariantCulture);
        if (text.Length > length)
            throw new OftpEncodingException($"Numeric value {value} exceeds field length {length}.");

        _stream.Write(Encoding.ASCII.GetBytes(text.PadLeft(length, '0')));
        return this;
    }

    /// <summary>Variable length UTF-8 text T(n) preceded by its octet length as 9(<paramref name="lengthDigits"/>).</summary>
    public CommandWriter TextWithLength(string? value, int lengthDigits = 3)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        var max = (int)Math.Pow(10, lengthDigits) - 1;
        if (bytes.Length > max)
            throw new OftpEncodingException($"Text exceeds maximum length of {max} octets.");

        Numeric(bytes.Length, lengthDigits);
        _stream.Write(bytes);
        return this;
    }

    /// <summary>Binary field U(n) preceded by its length as a 2 octet unsigned big-endian integer.</summary>
    public CommandWriter BinaryWithLength(ReadOnlySpan<byte> value)
    {
        if (value.Length > ushort.MaxValue)
            throw new OftpEncodingException("Binary value is too long.");

        _stream.WriteByte((byte)(value.Length >> 8));
        _stream.WriteByte((byte)value.Length);
        _stream.Write(value);
        return this;
    }

    public CommandWriter Raw(ReadOnlySpan<byte> value)
    {
        _stream.Write(value);
        return this;
    }

    public CommandWriter CarriageReturn()
    {
        _stream.WriteByte((byte)'\r');
        return this;
    }

    public byte[] ToArray() => _stream.ToArray();
}
