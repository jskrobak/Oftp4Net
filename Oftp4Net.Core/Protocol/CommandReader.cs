using System.Globalization;
using System.Text;

namespace Oftp4Net.Core.Protocol;

/// <summary>
/// Reads fields of an OFTP exchange buffer. The first octet (command indicator) is skipped.
/// </summary>
internal sealed class CommandReader(byte[] buffer)
{
    private int _position = 1;

    public bool AtEnd => _position >= buffer.Length;

    public string Alpha(int length)
    {
        var span = Take(length);
        return Encoding.ASCII.GetString(span).TrimEnd();
    }

    public long Numeric(int length)
    {
        var text = Encoding.ASCII.GetString(Take(length)).Trim();
        if (text.Length == 0)
            return 0;

        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            throw new OftpDecodingException($"Invalid numeric field value '{text}'.");

        return value;
    }

    public string TextWithLength(int lengthDigits = 3)
    {
        var length = (int)Numeric(lengthDigits);
        return Encoding.UTF8.GetString(Take(length));
    }

    /// <summary>Reads a binary field of a fixed length (e.g. AURPRSP).</summary>
    public byte[] Binary(int length) => Take(length).ToArray();

    public byte[] BinaryWithLength()
    {
        var header = Take(2);
        var length = (header[0] << 8) | header[1];
        return Take(length).ToArray();
    }

    public CommandReader Skip(int length)
    {
        Take(length);
        return this;
    }

    /// <summary>Consumes an optional trailing carriage return. Some implementations omit it.</summary>
    public void OptionalCarriageReturn()
    {
        if (!AtEnd && buffer[_position] == (byte)'\r')
            _position++;
    }

    private ReadOnlySpan<byte> Take(int length)
    {
        if (length < 0 || _position + length > buffer.Length)
            throw new OftpDecodingException(
                $"Command '{(char)buffer[0]}' is truncated: expected {length} more octets at position {_position}, buffer length {buffer.Length}.");

        var span = buffer.AsSpan(_position, length);
        _position += length;
        return span;
    }
}
