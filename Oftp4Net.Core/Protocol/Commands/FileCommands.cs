namespace Oftp4Net.Core.Protocol.Commands;

/// <summary>Start File.</summary>
public sealed class SFID : OftpCommand
{
    public const char Id = 'H';
    public override char Indicator => Id;

    public string DatasetName { get; init; } = "";
    /// <summary>Virtual file date stamp, CCYYMMDD.</summary>
    public string Date { get; init; } = "";
    /// <summary>Virtual file time stamp, HHMMSScccc.</summary>
    public string Time { get; init; } = "";
    public string UserData { get; init; } = "";
    public string Destination { get; init; } = "";
    public string Originator { get; init; } = "";
    public string Format { get; init; } = FileFormats.Unstructured;
    public int MaxRecordSize { get; init; }
    /// <summary>File size in 1K blocks.</summary>
    public long FileSize { get; init; }
    /// <summary>Original (uncompressed, unencrypted) file size in 1K blocks.</summary>
    public long OriginalFileSize { get; init; }
    public long RestartPosition { get; init; }
    public string SecurityLevel { get; init; } = SecurityLevels.None;
    public string CipherSuite { get; init; } = CipherSuites.None;
    public string Compression { get; init; } = FileCompressionAlgorithms.None;
    public string Enveloping { get; init; } = FileEnvelopingFormats.None;
    public bool SignedEerpRequested { get; init; }
    public string Description { get; init; } = "";

    internal override void Write(CommandWriter writer) => writer
        .Alpha(DatasetName, 26)
        .Alpha("", 3)
        .Alpha(Date, 8)
        .Alpha(Time, 10)
        .Alpha(UserData, 8)
        .Alpha(Destination, 25)
        .Alpha(Originator, 25)
        .Alpha(Format, 1)
        .Numeric(MaxRecordSize, 5)
        .Numeric(FileSize, 13)
        .Numeric(OriginalFileSize, 13)
        .Numeric(RestartPosition, 17)
        .Alpha(SecurityLevel, 2)
        .Alpha(CipherSuite, 2)
        .Alpha(Compression, 1)
        .Alpha(Enveloping, 1)
        .Alpha(YesNo(SignedEerpRequested), 1)
        .TextWithLength(Description);

    internal static SFID Read(CommandReader reader) => new()
    {
        DatasetName = reader.Alpha(26),
        Date = reader.Skip(3).Alpha(8),
        Time = reader.Alpha(10),
        UserData = reader.Alpha(8),
        Destination = reader.Alpha(25),
        Originator = reader.Alpha(25),
        Format = reader.Alpha(1),
        MaxRecordSize = (int)reader.Numeric(5),
        FileSize = reader.Numeric(13),
        OriginalFileSize = reader.Numeric(13),
        RestartPosition = reader.Numeric(17),
        SecurityLevel = reader.Alpha(2),
        CipherSuite = reader.Alpha(2),
        Compression = reader.Alpha(1),
        Enveloping = reader.Alpha(1),
        SignedEerpRequested = ParseYesNo(reader.Alpha(1)),
        Description = reader.AtEnd ? "" : reader.TextWithLength(),
    };
}

/// <summary>Start File Positive Answer.</summary>
public sealed class SFPA : OftpCommand
{
    public const char Id = '2';
    public override char Indicator => Id;

    /// <summary>Restart position accepted by the receiver.</summary>
    public long AnswerCount { get; init; }

    internal override void Write(CommandWriter writer) => writer.Numeric(AnswerCount, 17);

    internal static SFPA Read(CommandReader reader) => new() { AnswerCount = reader.Numeric(17) };
}

/// <summary>Start File Negative Answer.</summary>
public sealed class SFNA : OftpCommand
{
    public const char Id = '3';
    public override char Indicator => Id;

    public string ReasonCode { get; init; } = AnswerReasonCodes.UnspecifiedReason;
    public bool RetryLater { get; init; }
    public string ReasonText { get; init; } = "";

    internal override void Write(CommandWriter writer) => writer
        .Numeric(int.Parse(ReasonCode), 2)
        .Alpha(YesNo(RetryLater), 1)
        .TextWithLength(ReasonText);

    internal static SFNA Read(CommandReader reader) => new()
    {
        ReasonCode = reader.Numeric(2).ToString("00"),
        RetryLater = ParseYesNo(reader.Alpha(1)),
        ReasonText = reader.AtEnd ? "" : reader.TextWithLength(),
    };
}

/// <summary>Set Credit.</summary>
public sealed class CDT : OftpCommand
{
    public const char Id = 'C';
    public override char Indicator => Id;

    internal override void Write(CommandWriter writer) => writer.Alpha("", 2);

    internal static CDT Read(CommandReader reader)
    {
        // The two reserved octets are sometimes omitted by other implementations.
        return new CDT();
    }
}

/// <summary>End File.</summary>
public sealed class EFID : OftpCommand
{
    public const char Id = 'T';
    public override char Indicator => Id;

    public long RecordCount { get; init; }
    /// <summary>Exact number of octets transmitted.</summary>
    public long UnitCount { get; init; }

    internal override void Write(CommandWriter writer) => writer
        .Numeric(RecordCount, 17)
        .Numeric(UnitCount, 17);

    internal static EFID Read(CommandReader reader) => new()
    {
        RecordCount = reader.Numeric(17),
        UnitCount = reader.Numeric(17),
    };
}

/// <summary>End File Positive Answer.</summary>
public sealed class EFPA : OftpCommand
{
    public const char Id = '4';
    public override char Indicator => Id;

    /// <summary>The listener asks the speaker to change direction (send CD).</summary>
    public bool ChangeDirection { get; init; }

    internal override void Write(CommandWriter writer) => writer.Alpha(YesNo(ChangeDirection), 1);

    internal static EFPA Read(CommandReader reader) => new() { ChangeDirection = ParseYesNo(reader.Alpha(1)) };
}

/// <summary>End File Negative Answer.</summary>
public sealed class EFNA : OftpCommand
{
    public const char Id = '5';
    public override char Indicator => Id;

    public string ReasonCode { get; init; } = AnswerReasonCodes.UnspecifiedReason;
    public string ReasonText { get; init; } = "";

    internal override void Write(CommandWriter writer) => writer
        .Numeric(int.Parse(ReasonCode), 2)
        .TextWithLength(ReasonText);

    internal static EFNA Read(CommandReader reader) => new()
    {
        ReasonCode = reader.Numeric(2).ToString("00"),
        ReasonText = reader.AtEnd ? "" : reader.TextWithLength(),
    };
}

/// <summary>End to End Response, confirms that a file reached its final destination.</summary>
public sealed class EERP : OftpCommand
{
    public const char Id = 'E';
    public override char Indicator => Id;

    public string DatasetName { get; init; } = "";
    public string Date { get; init; } = "";
    public string Time { get; init; } = "";
    public string UserData { get; init; } = "";
    /// <summary>Originator of the virtual file (the EERP travels back to it).</summary>
    public string Destination { get; init; } = "";
    /// <summary>Final destination of the virtual file (the creator of this EERP).</summary>
    public string Originator { get; init; } = "";
    public byte[] Hash { get; init; } = [];
    public byte[] Signature { get; init; } = [];

    /// <summary>Creates the EERP confirming delivery of the file described by <paramref name="file"/>.</summary>
    public static EERP For(SFID file) => new()
    {
        DatasetName = file.DatasetName,
        Date = file.Date,
        Time = file.Time,
        UserData = file.UserData,
        Destination = file.Originator,
        Originator = file.Destination,
    };

    internal override void Write(CommandWriter writer) => writer
        .Alpha(DatasetName, 26)
        .Alpha("", 3)
        .Alpha(Date, 8)
        .Alpha(Time, 10)
        .Alpha(UserData, 8)
        .Alpha(Destination, 25)
        .Alpha(Originator, 25)
        .BinaryWithLength(Hash)
        .BinaryWithLength(Signature);

    internal static EERP Read(CommandReader reader) => new()
    {
        DatasetName = reader.Alpha(26),
        Date = reader.Skip(3).Alpha(8),
        Time = reader.Alpha(10),
        UserData = reader.Alpha(8),
        Destination = reader.Alpha(25),
        Originator = reader.Alpha(25),
        Hash = reader.AtEnd ? [] : reader.BinaryWithLength(),
        Signature = reader.AtEnd ? [] : reader.BinaryWithLength(),
    };
}

/// <summary>Negative End Response, reports that a file could not be delivered to its final destination.</summary>
public sealed class NERP : OftpCommand
{
    public const char Id = 'N';
    public override char Indicator => Id;

    public string DatasetName { get; init; } = "";
    public string Date { get; init; } = "";
    public string Time { get; init; } = "";
    public string Destination { get; init; } = "";
    public string Originator { get; init; } = "";
    /// <summary>Identification of the node that created this NERP.</summary>
    public string Creator { get; init; } = "";
    public string ReasonCode { get; init; } = AnswerReasonCodes.UnspecifiedReason;
    public string ReasonText { get; init; } = "";
    public byte[] Hash { get; init; } = [];
    public byte[] Signature { get; init; } = [];

    internal override void Write(CommandWriter writer) => writer
        .Alpha(DatasetName, 26)
        .Alpha("", 6)
        .Alpha(Date, 8)
        .Alpha(Time, 10)
        .Alpha(Destination, 25)
        .Alpha(Originator, 25)
        .Alpha(Creator, 25)
        .Numeric(int.Parse(ReasonCode), 2)
        .TextWithLength(ReasonText)
        .BinaryWithLength(Hash)
        .BinaryWithLength(Signature);

    internal static NERP Read(CommandReader reader) => new()
    {
        DatasetName = reader.Alpha(26),
        Date = reader.Skip(6).Alpha(8),
        Time = reader.Alpha(10),
        Destination = reader.Alpha(25),
        Originator = reader.Alpha(25),
        Creator = reader.Alpha(25),
        ReasonCode = reader.Numeric(2).ToString("00"),
        ReasonText = reader.TextWithLength(),
        Hash = reader.AtEnd ? [] : reader.BinaryWithLength(),
        Signature = reader.AtEnd ? [] : reader.BinaryWithLength(),
    };
}
