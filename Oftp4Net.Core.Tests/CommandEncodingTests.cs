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

    #region Protocol release levels (RFC 2204 layouts)

    [Fact]
    public void SSID_OfRevision13_HasNoSecureAuthenticationField()
    {
        var ssid = new SSID
        {
            Level = ProtocolLevels.Oftp13,
            Code = "O0013000000TEST",
            Password = "PWD",
            ExchangeBufferSize = 2048,
            Credit = 7,
            UserData = "USER",
        };

        var encoded = ssid.Encode(ProtocolLevels.Oftp13);

        // Command, level, code, password, buffer size, S/R, compression, restart, special logic, credit,
        // 5 reserved octets, user data and the carriage return (RFC 2204, section 5.3.2).
        Assert.Equal(61, encoded.Length);
        Assert.Equal("2", Ascii(encoded[1..2]));
        Assert.Equal("USER    ", Ascii(encoded[52..60]));
        Assert.Equal("\r", Ascii(encoded[60..61]));

        var decoded = Assert.IsType<SSID>(OftpCommand.Decode(encoded, ProtocolLevels.Oftp13));
        Assert.Equal(ProtocolLevels.Oftp13, decoded.Level);
        Assert.Equal("O0013000000TEST", decoded.Code);
        Assert.Equal(2048, decoded.ExchangeBufferSize);
        Assert.Equal(7, decoded.Credit);
        Assert.Equal("USER", decoded.UserData);
        Assert.False(decoded.SecureAuthentication);
    }

    [Fact]
    public void SFID_OfRevision13_MatchesRfc2204Layout()
    {
        var sfid = new SFID
        {
            DatasetName = "TESTFILE",
            Date = "20260923",
            Time = "1122334455",
            UserData = "USER",
            Destination = "O0013000000DEST",
            Originator = "O0013000000ORIG",
            FileSize = 42,
            OriginalFileSize = 42,
            RestartPosition = 7,
            // Attributes of OFTP 2.0 are not part of the older layout.
            SecurityLevel = SecurityLevels.EncryptedAndSigned,
            Description = "ignored",
        };

        var encoded = sfid.Encode(ProtocolLevels.Oftp13);

        Assert.Equal(128, encoded.Length);
        // The stamps are shortened and the reserved area before them is longer.
        Assert.Equal("260923", Ascii(encoded[36..42]));
        Assert.Equal("112233", Ascii(encoded[42..48]));
        Assert.Equal("USER    ", Ascii(encoded[48..56]));
        Assert.Equal("0000042", Ascii(encoded[112..119]));
        Assert.Equal("000000007", Ascii(encoded[119..128]));

        var decoded = Assert.IsType<SFID>(OftpCommand.Decode(encoded, ProtocolLevels.Oftp13));
        Assert.Equal("TESTFILE", decoded.DatasetName);
        Assert.Equal("260923", decoded.Date);
        Assert.Equal("112233", decoded.Time);
        Assert.Equal(42, decoded.FileSize);
        Assert.Equal(7, decoded.RestartPosition);
        Assert.Equal(SecurityLevels.None, decoded.SecurityLevel);
        Assert.Equal("", decoded.Description);
    }

    [Fact]
    public void SFID_OfRevision14_KeepsTheExtendedStampsAndTheShortSizes()
    {
        var sfid = new SFID { DatasetName = "F", Date = "20260923", Time = "1122334455", FileSize = 3 };

        var encoded = sfid.Encode(ProtocolLevels.Oftp14);

        // Revision 1.4 only extended the stamps, the fields behind them keep their positions.
        Assert.Equal(128, encoded.Length);
        Assert.Equal("20260923", Ascii(encoded[30..38]));
        Assert.Equal("1122334455", Ascii(encoded[38..48]));

        var decoded = Assert.IsType<SFID>(OftpCommand.Decode(encoded, ProtocolLevels.Oftp14));
        Assert.Equal("20260923", decoded.Date);
        Assert.Equal("1122334455", decoded.Time);
        Assert.Equal(3, decoded.FileSize);
    }

    [Fact]
    public void EERP_OfRevision13_HasNoHashAndSignature()
    {
        var eerp = new EERP
        {
            DatasetName = "TESTFILE",
            Date = "20260923",
            Time = "1122334455",
            UserData = "USER",
            Destination = "O0013000000DEST",
            Originator = "O0013000000ORIG",
            Hash = [1, 2, 3],
            Signature = [4, 5, 6],
        };

        var encoded = eerp.Encode(ProtocolLevels.Oftp13);

        Assert.Equal(106, encoded.Length);
        Assert.Equal("260923", Ascii(encoded[36..42]));

        var decoded = Assert.IsType<EERP>(OftpCommand.Decode(encoded, ProtocolLevels.Oftp13));
        Assert.Equal("TESTFILE", decoded.DatasetName);
        Assert.Equal("260923", decoded.Date);
        Assert.Equal("112233", decoded.Time);
        Assert.Empty(decoded.Hash);
        Assert.Empty(decoded.Signature);
    }

    [Fact]
    public void AnswersOfRevision13_CarryNoReasonText()
    {
        var sfna = new SFNA { ReasonCode = AnswerReasonCodes.DuplicateFile, RetryLater = true, ReasonText = "ignored" };
        var efna = new EFNA { ReasonCode = AnswerReasonCodes.InvalidByteCount, ReasonText = "ignored" };
        var esid = new ESID { ReasonCode = ReasonCodes.InvalidPassword, ReasonText = "ignored" };

        Assert.Equal("3", Ascii(sfna.Encode(ProtocolLevels.Oftp13)[..1]));
        Assert.Equal(4, sfna.Encode(ProtocolLevels.Oftp13).Length);
        Assert.Equal(3, efna.Encode(ProtocolLevels.Oftp13).Length);
        Assert.Equal("F04\r", Ascii(esid.Encode(ProtocolLevels.Oftp13)));

        var decoded = Assert.IsType<SFNA>(OftpCommand.Decode(sfna.Encode(ProtocolLevels.Oftp13), ProtocolLevels.Oftp13));
        Assert.Equal(AnswerReasonCodes.DuplicateFile, decoded.ReasonCode);
        Assert.True(decoded.RetryLater);
        Assert.Equal("", decoded.ReasonText);
    }

    [Fact]
    public void ReasonCodesUnknownToTheLevelBecomeUnspecified()
    {
        // The codes of file level security exist from OFTP 2.0 on, code 14 from revision 1.4 on.
        var security = new EFNA { ReasonCode = AnswerReasonCodes.InvalidFileSignature };
        var direction = new SFNA { ReasonCode = AnswerReasonCodes.FileDirectionRefused };

        Assert.Equal("99", Ascii(security.Encode(ProtocolLevels.Oftp14)[1..3]));
        Assert.Equal("14", Ascii(direction.Encode(ProtocolLevels.Oftp14)[1..3]));
        Assert.Equal("99", Ascii(direction.Encode(ProtocolLevels.Oftp13)[1..3]));
        Assert.Equal("21", Ascii(security.Encode(ProtocolLevels.Oftp2)[1..3]));
    }

    [Fact]
    public void EfidAndSfpa_OfRevision13_UseShorterCounts()
    {
        var efid = new EFID { RecordCount = 5, UnitCount = 1234 };
        var sfpa = new SFPA { AnswerCount = 9 };

        Assert.Equal("T000000005000000001234", Ascii(efid.Encode(ProtocolLevels.Oftp13)));
        Assert.Equal("2000000009", Ascii(sfpa.Encode(ProtocolLevels.Oftp13)));

        Assert.Equal(1234, Assert.IsType<EFID>(
            OftpCommand.Decode(efid.Encode(ProtocolLevels.Oftp13), ProtocolLevels.Oftp13)).UnitCount);
        Assert.Equal(9, Assert.IsType<SFPA>(
            OftpCommand.Decode(sfpa.Encode(ProtocolLevels.Oftp13), ProtocolLevels.Oftp13)).AnswerCount);
    }

    #endregion
}
