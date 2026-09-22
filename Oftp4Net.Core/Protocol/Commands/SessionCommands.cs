namespace Oftp4Net.Core.Protocol.Commands;

/// <summary>Start Session Ready Message, sent by the Responder after the connection is established.</summary>
public sealed class SSRM : OftpCommand
{
    public const char Id = 'I';
    public const string ReadyMessage = "ODETTE FTP READY";

    public override char Indicator => Id;

    public string Message { get; init; } = ReadyMessage;

    internal override void Write(CommandWriter writer) =>
        writer.Alpha(Message, 17).CarriageReturn();

    internal static SSRM Read(CommandReader reader)
    {
        var message = reader.Alpha(17);
        reader.OptionalCarriageReturn();
        return new SSRM { Message = message };
    }
}

/// <summary>Start Session.</summary>
public sealed class SSID : OftpCommand
{
    public const char Id = 'X';

    /// <summary>Protocol release level, 5 = OFTP 2.0.</summary>
    public const int Oftp2Level = 5;

    public override char Indicator => Id;

    public int Level { get; init; } = Oftp2Level;
    public string Code { get; init; } = "";
    public string Password { get; init; } = "";
    public int ExchangeBufferSize { get; init; } = OftpTransport.MaxExchangeBufferSize;
    public string SendReceive { get; init; } = SendReceiveCapabilities.Both;
    public bool BufferCompression { get; init; }
    public bool Restart { get; init; }
    public bool SpecialLogic { get; init; }
    public int Credit { get; init; } = 99;
    public bool SecureAuthentication { get; init; }
    public string UserData { get; init; } = "";

    internal override void Write(CommandWriter writer) => writer
        .Numeric(Level, 1)
        .Alpha(Code, 25)
        .Alpha(Password, 8)
        .Numeric(ExchangeBufferSize, 5)
        .Alpha(SendReceive, 1)
        .Alpha(YesNo(BufferCompression), 1)
        .Alpha(YesNo(Restart), 1)
        .Alpha(YesNo(SpecialLogic), 1)
        .Numeric(Credit, 3)
        .Alpha(YesNo(SecureAuthentication), 1)
        .Alpha("", 4)
        .Alpha(UserData, 8)
        .CarriageReturn();

    internal static SSID Read(CommandReader reader)
    {
        // Object initializers evaluate in declaration order, which matches the field order of the command.
        var ssid = new SSID
        {
            Level = (int)reader.Numeric(1),
            Code = reader.Alpha(25),
            Password = reader.Alpha(8),
            ExchangeBufferSize = (int)reader.Numeric(5),
            SendReceive = reader.Alpha(1),
            BufferCompression = ParseYesNo(reader.Alpha(1)),
            Restart = ParseYesNo(reader.Alpha(1)),
            SpecialLogic = ParseYesNo(reader.Alpha(1)),
            Credit = (int)reader.Numeric(3),
            SecureAuthentication = ParseYesNo(reader.Alpha(1)),
            UserData = reader.Skip(4).Alpha(8),
        };
        reader.OptionalCarriageReturn();
        return ssid;
    }
}

/// <summary>End Session.</summary>
public sealed class ESID : OftpCommand
{
    public const char Id = 'F';

    public override char Indicator => Id;

    public string ReasonCode { get; init; } = ReasonCodes.NormalTermination;
    public string ReasonText { get; init; } = "";

    internal override void Write(CommandWriter writer) => writer
        .Numeric(int.Parse(ReasonCode), 2)
        .TextWithLength(ReasonText)
        .CarriageReturn();

    internal static ESID Read(CommandReader reader)
    {
        var code = reader.Numeric(2).ToString("00");
        var text = reader.AtEnd ? "" : reader.TextWithLength();
        reader.OptionalCarriageReturn();
        return new ESID { ReasonCode = code, ReasonText = text };
    }
}

/// <summary>Change Direction.</summary>
public sealed class CD : OftpCommand
{
    public const char Id = 'R';
    public override char Indicator => Id;
    internal override void Write(CommandWriter writer) { }
}

/// <summary>Ready To Receive, the answer to EERP and NERP.</summary>
public sealed class RTR : OftpCommand
{
    public const char Id = 'P';
    public override char Indicator => Id;
    internal override void Write(CommandWriter writer) { }
}

/// <summary>Security Change Direction, used by secure authentication.</summary>
public sealed class SECD : OftpCommand
{
    public const char Id = 'J';
    public override char Indicator => Id;
    internal override void Write(CommandWriter writer) { }
}

/// <summary>Authentication Challenge.</summary>
public sealed class AUCH : OftpCommand
{
    public const char Id = 'A';
    public override char Indicator => Id;

    public byte[] Challenge { get; init; } = [];

    internal override void Write(CommandWriter writer) => writer.BinaryWithLength(Challenge);

    internal static AUCH Read(CommandReader reader) => new() { Challenge = reader.BinaryWithLength() };
}

/// <summary>Authentication Response.</summary>
public sealed class AURP : OftpCommand
{
    public const char Id = 'S';
    public override char Indicator => Id;

    public string Response { get; init; } = "";

    internal override void Write(CommandWriter writer) => writer.Alpha(Response, 20);

    internal static AURP Read(CommandReader reader) => new() { Response = reader.Alpha(20) };
}
