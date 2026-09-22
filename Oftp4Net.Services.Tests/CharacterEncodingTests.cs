using System.Text;
using Oftp4Net.Domain;
using Oftp4Net.Services.Encodings;

namespace Oftp4Net.Services.Tests;

public class CharacterEncodingTests
{
    private const int Ansi = CharacterEncodings.DefaultAnsiCodePage;
    private const int Ebcdic = CharacterEncodings.DefaultEbcdicCodePage;

    private static byte[] ReadAll(Stream stream)
    {
        var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    [Fact]
    public void AnsiIsTranslatedToEbcdic()
    {
        var table = CharacterEncodings.GetAnsiToEbcdicTable(Ansi, Ebcdic);
        var content = "AB 09"u8.ToArray();

        var translated = content.Select(b => table[b]).ToArray();

        // IBM500: 'A' = 0xC1, 'B' = 0xC2, space = 0x40, '0' = 0xF0, '9' = 0xF9.
        Assert.Equal(new byte[] { 0xC1, 0xC2, 0x40, 0xF0, 0xF9 }, translated);
    }

    [Fact]
    public void EbcdicIsTranslatedToAnsi()
    {
        var table = CharacterEncodings.GetEbcdicToAnsiTable(Ebcdic, Ansi);
        var content = new byte[] { 0xC1, 0xC2, 0x40, 0xF0, 0xF9 };

        var translated = content.Select(b => table[b]).ToArray();

        Assert.Equal("AB 09", Encoding.ASCII.GetString(translated));
    }

    [Theory]
    [InlineData(0x25, (byte)'\n')] // EBCDIC line feed
    [InlineData(0x15, (byte)'\n')] // EBCDIC new line, replaced by a line feed
    [InlineData(0x0D, (byte)'\r')]
    public void EbcdicLineEndingsBecomeAnsiLineEndings(int ebcdic, byte expected)
    {
        var table = CharacterEncodings.GetEbcdicToAnsiTable(Ebcdic, Ansi);

        Assert.Equal(expected, table[ebcdic]);
    }

    [Fact]
    public void AnsiLineEndingsSurviveTheRoundTrip()
    {
        var toEbcdic = CharacterEncodings.GetAnsiToEbcdicTable(Ansi, Ebcdic);
        var toAnsi = CharacterEncodings.GetEbcdicToAnsiTable(Ebcdic, Ansi);
        var content = "line one\r\nline two\n"u8.ToArray();

        var roundTripped = content.Select(b => toAnsi[toEbcdic[b]]).ToArray();

        Assert.Equal(content, roundTripped);
    }

    [Fact]
    public void CharactersMissingInTheTargetCodePageAreReplaced()
    {
        var table = CharacterEncodings.GetAnsiToEbcdicTable(1250, Ebcdic);
        var ansi = Encoding.GetEncoding(1250);

        // 'ř' exists in Windows-1250 but not in IBM500; it is replaced by a question mark (0x6F).
        Assert.Equal(0x6F, table[ansi.GetBytes("ř")[0]]);
    }

    [Fact]
    public void UnknownCodePageIsReported()
    {
        var exception = Assert.Throws<NotSupportedException>(() => CharacterEncodings.GetAnsiToEbcdicTable(Ansi, 12345));

        Assert.Contains("12345", exception.Message);
    }

    [Fact]
    public void MultiByteCodePageIsRefused()
    {
        Assert.Throws<NotSupportedException>(() => CharacterEncodings.GetAnsiToEbcdicTable(65001, Ebcdic));
    }

    [Fact]
    public void ReadingTranslatesTheContentAndKeepsTheLength()
    {
        var content = "Hello"u8.ToArray();
        using var source = new MemoryStream(content);
        using var stream = new ByteTranslatingStream(source, CharacterEncodings.GetAnsiToEbcdicTable(Ansi, Ebcdic));

        var read = ReadAll(stream);

        Assert.Equal(content.Length, read.Length);
        Assert.Equal(Encoding.GetEncoding(Ebcdic).GetBytes("Hello"), read);
        Assert.Equal(content.Length, stream.Length);
    }

    [Fact]
    public async Task WritingTranslatesTheContent()
    {
        var destination = new MemoryStream();
        var stream = new ByteTranslatingStream(destination, CharacterEncodings.GetEbcdicToAnsiTable(Ebcdic, Ansi));

        await stream.WriteAsync(Encoding.GetEncoding(Ebcdic).GetBytes("Hello"));
        await stream.FlushAsync();

        Assert.Equal("Hello", Encoding.ASCII.GetString(destination.ToArray()));
    }

    [Fact]
    public void SendingUsesTheEncodingOfThePartner()
    {
        var content = "Hello"u8.ToArray();
        var partner = new Partner { OutgoingEncoding = FileCharacterEncoding.EBCDIC };

        using var ansi = new MemoryStream(content);
        Assert.Same(ansi, PartnerEncoding.ForSending(new Partner(), ansi));

        using var source = new MemoryStream(content);
        using var ebcdic = PartnerEncoding.ForSending(partner, source);
        Assert.Equal(Encoding.GetEncoding(Ebcdic).GetBytes("Hello"), ReadAll(ebcdic));
    }

    [Fact]
    public async Task ReceivingConvertsOnlyWhenConfigured()
    {
        var content = Encoding.GetEncoding(Ebcdic).GetBytes("Hello");

        var stored = new MemoryStream();
        var unconverted = PartnerEncoding.ForReceiving(new Partner(), stored);
        Assert.Same(stored, unconverted);
        await unconverted.WriteAsync(content);
        Assert.Equal(content, stored.ToArray());

        var converted = new MemoryStream();
        var partner = new Partner { ConvertIncomingEbcdicToAnsi = true };
        await using var stream = PartnerEncoding.ForReceiving(partner, converted);
        await stream.WriteAsync(content);
        await stream.FlushAsync();
        Assert.Equal("Hello", Encoding.ASCII.GetString(converted.ToArray()));
    }
}
