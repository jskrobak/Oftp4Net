using System.Text;
using Oftp4Net.Core.Protocol;
using Oftp4Net.Core.Protocol.Commands;

namespace Oftp4Net.Core.Tests;

public class CommandEncodingTests
{
    private static string Ascii(byte[] bytes) => Encoding.ASCII.GetString(bytes);

    [Fact]
    public void SSRM_MatchesRfcLayout()
    {
        Assert.Equal("IODETTE FTP READY \r", Ascii(new SSRM().Encode()));
    }

    [Fact]
    public void SSID_MatchesRfcLayout()
    {
        var ssid = new SSID
        {
            Code = "O0013000000ACME",
            Password = "SECRET",
            ExchangeBufferSize = 4096,
            Credit = 64,
        };

        var encoded = Ascii(ssid.Encode());

        Assert.Equal(61, encoded.Length);
        Assert.Equal("X5O0013000000ACME          SECRET  04096BNNN064N            \r", encoded);
    }

    [Fact]
    public void StreamTransmissionHeader_HasVersionOneAndTotalLength()
    {
        Assert.Equal(new byte[] { 0x10, 0x00, 0x00, 0x17 }, OftpTransport.CreateHeader(19));
        Assert.Equal(new byte[] { 0x10, 0x01, 0x86, 0xA3 }, OftpTransport.CreateHeader(99999));
        Assert.Throws<ArgumentOutOfRangeException>(() => OftpTransport.CreateHeader(100000));
    }

    [Fact]
    public void SFID_RoundTrip_WithUtf8Description()
    {
        var sfid = new SFID
        {
            DatasetName = "DELFOR.TEST",
            Date = "20250101",
            Time = "1230450001",
            UserData = "USER",
            Destination = "O0013000000DEST",
            Originator = "O0013000000ORIG",
            FileSize = 12,
            OriginalFileSize = 12,
            Description = "Příliš žluťoučký kůň",
        };

        var encoded = sfid.Encode();
        var decoded = Assert.IsType<SFID>(OftpCommand.Decode(encoded));

        Assert.Equal('H', (char)encoded[0]);
        Assert.Equal(1 + 26 + 3 + 8 + 10 + 8 + 25 + 25 + 1 + 5 + 13 + 13 + 17 + 2 + 2 + 1 + 1 + 1 + 3
                     + Encoding.UTF8.GetByteCount(sfid.Description), encoded.Length);
        Assert.Equal(sfid.DatasetName, decoded.DatasetName);
        Assert.Equal(sfid.Date, decoded.Date);
        Assert.Equal(sfid.Time, decoded.Time);
        Assert.Equal(sfid.Destination, decoded.Destination);
        Assert.Equal(sfid.Originator, decoded.Originator);
        Assert.Equal(12, decoded.FileSize);
        Assert.Equal(sfid.Description, decoded.Description);
    }

    [Fact]
    public void EERP_RoundTrip_WithBinaryFields()
    {
        var eerp = new EERP
        {
            DatasetName = "FILE",
            Date = "20250101",
            Time = "1230450001",
            Destination = "ORIG",
            Originator = "DEST",
            Hash = [1, 2, 3],
        };

        var decoded = Assert.IsType<EERP>(OftpCommand.Decode(eerp.Encode()));

        Assert.Equal("FILE", decoded.DatasetName);
        Assert.Equal("ORIG", decoded.Destination);
        Assert.Equal(new byte[] { 1, 2, 3 }, decoded.Hash);
        Assert.Empty(decoded.Signature);
    }

    [Fact]
    public void NERP_RoundTrip()
    {
        var nerp = new NERP
        {
            DatasetName = "FILE",
            Date = "20250101",
            Time = "1230450001",
            Destination = "ORIG",
            Originator = "DEST",
            Creator = "DEST",
            ReasonCode = AnswerReasonCodes.InvalidDestination,
            ReasonText = "Unknown",
        };

        var decoded = Assert.IsType<NERP>(OftpCommand.Decode(nerp.Encode()));

        Assert.Equal("02", decoded.ReasonCode);
        Assert.Equal("Unknown", decoded.ReasonText);
        Assert.Equal("DEST", decoded.Creator);
    }

    [Theory]
    [InlineData("00", "")]
    [InlineData("04", "Invalid password")]
    public void ESID_RoundTrip(string code, string text)
    {
        var decoded = Assert.IsType<ESID>(OftpCommand.Decode(new ESID { ReasonCode = code, ReasonText = text }.Encode()));
        Assert.Equal(code, decoded.ReasonCode);
        Assert.Equal(text, decoded.ReasonText);
    }

    [Fact]
    public void ESID_WithoutCarriageReturn_IsAccepted()
    {
        var decoded = Assert.IsType<ESID>(OftpCommand.Decode(Encoding.ASCII.GetBytes("F00000")));
        Assert.Equal("00", decoded.ReasonCode);
    }

    [Fact]
    public void SimpleCommands_RoundTrip()
    {
        Assert.IsType<CD>(OftpCommand.Decode(new CD().Encode()));
        Assert.IsType<RTR>(OftpCommand.Decode(new RTR().Encode()));
        Assert.IsType<CDT>(OftpCommand.Decode(new CDT().Encode()));
        Assert.Equal(3, new CDT().Encode().Length);
        Assert.Equal(18, new SFPA().Encode().Length);
        Assert.True(Assert.IsType<EFPA>(OftpCommand.Decode(new EFPA { ChangeDirection = true }.Encode())).ChangeDirection);

        var efid = Assert.IsType<EFID>(OftpCommand.Decode(new EFID { UnitCount = 123456 }.Encode()));
        Assert.Equal(123456, efid.UnitCount);

        var sfna = Assert.IsType<SFNA>(OftpCommand.Decode(new SFNA { ReasonCode = "13", RetryLater = true, ReasonText = "Dup" }.Encode()));
        Assert.Equal("13", sfna.ReasonCode);
        Assert.True(sfna.RetryLater);

        var auch = Assert.IsType<AUCH>(OftpCommand.Decode(new AUCH { Challenge = [9, 8, 7] }.Encode()));
        Assert.Equal(new byte[] { 9, 8, 7 }, auch.Challenge);
    }

    [Fact]
    public void UnknownIndicator_IsRejected()
    {
        var ex = Assert.Throws<OftpProtocolException>(() => OftpCommand.Decode([(byte)'Z']));
        Assert.Equal(ReasonCodes.CommandNotRecognised, ex.ReasonCode);
    }

    [Fact]
    public void TooLongField_IsRejected()
    {
        Assert.Throws<OftpEncodingException>(() => new SSID { Code = new string('A', 26) }.Encode());
    }

    [Fact]
    public void TruncatedCommand_IsRejected()
    {
        Assert.Throws<OftpDecodingException>(() => OftpCommand.Decode(Encoding.ASCII.GetBytes("X5ABC")));
    }
}
