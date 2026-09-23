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

    internal override void Write(CommandWriter writer)
    {
        writer.Alpha(DatasetName, 26);
        WriteTimestamp(writer, Date, Time);
        writer
            .Alpha(UserData, 8)
            .Alpha(Destination, 25)
            .Alpha(Originator, 25)
            .Alpha(Format, 1)
            .Numeric(MaxRecordSize, 5);

        // The sizes are shorter before OFTP 2.0, which also added the original size and the security fields.
        if (!ProtocolLevels.HasOftp2Features(writer.Level))
        {
            writer.Numeric(Math.Min(FileSize, 9_999_999), 7).Numeric(Math.Min(RestartPosition, 999_999_999), 9);
            return;
        }

        writer
            .Numeric(FileSize, 13)
            .Numeric(OriginalFileSize, 13)
            .Numeric(RestartPosition, 17)
            .Alpha(SecurityLevel, 2)
            .Alpha(CipherSuite, 2)
            .Alpha(Compression, 1)
            .Alpha(Enveloping, 1)
            .Alpha(YesNo(SignedEerpRequested), 1)
            .TextWithLength(Description);
    }

    internal static SFID Read(CommandReader reader)
    {
        var datasetName = reader.Alpha(26);
        var (date, time) = ReadTimestamp(reader);
        var userData = reader.Alpha(8);
        var destination = reader.Alpha(25);
        var originator = reader.Alpha(25);
        var format = reader.Alpha(1);
        var maxRecordSize = (int)reader.Numeric(5);

        // Before OFTP 2.0 the sizes are shorter and the original size and the security fields do not exist.
        var oftp2 = ProtocolLevels.HasOftp2Features(reader.Level);
        var fileSize = reader.Numeric(oftp2 ? 13 : 7);

        return new SFID
        {
            DatasetName = datasetName,
            Date = date,
            Time = time,
            UserData = userData,
            Destination = destination,
            Originator = originator,
            Format = format,
            MaxRecordSize = maxRecordSize,
            FileSize = fileSize,
            OriginalFileSize = oftp2 ? reader.Numeric(13) : fileSize,
            RestartPosition = reader.Numeric(oftp2 ? 17 : 9),
            SecurityLevel = oftp2 ? reader.Alpha(2) : SecurityLevels.None,
            CipherSuite = oftp2 ? reader.Alpha(2) : CipherSuites.None,
            Compression = oftp2 ? reader.Alpha(1) : FileCompressionAlgorithms.None,
            Enveloping = oftp2 ? reader.Alpha(1) : FileEnvelopingFormats.None,
            SignedEerpRequested = oftp2 && ParseYesNo(reader.Alpha(1)),
            Description = oftp2 && !reader.AtEnd ? reader.TextWithLength() : "",
        };
    }
}

/// <summary>Start File Positive Answer.</summary>
public sealed class SFPA : OftpCommand
{
    public const char Id = '2';
    public override char Indicator => Id;

    /// <summary>Restart position accepted by the receiver.</summary>
    public long AnswerCount { get; init; }

    internal override void Write(CommandWriter writer) =>
        writer.Numeric(AnswerCount, ProtocolLevels.HasOftp2Features(writer.Level) ? 17 : 9);

    internal static SFPA Read(CommandReader reader) =>
        new() { AnswerCount = reader.Numeric(ProtocolLevels.HasOftp2Features(reader.Level) ? 17 : 9) };
}

/// <summary>Start File Negative Answer.</summary>
public sealed class SFNA : OftpCommand
{
    public const char Id = '3';
    public override char Indicator => Id;

    public string ReasonCode { get; init; } = AnswerReasonCodes.UnspecifiedReason;
    public bool RetryLater { get; init; }
    public string ReasonText { get; init; } = "";

    internal override void Write(CommandWriter writer)
    {
        writer.Numeric(int.Parse(LevelReasonCode(ReasonCode, writer.Level)), 2).Alpha(YesNo(RetryLater), 1);

        // The reason text was added in OFTP 2.0.
        if (ProtocolLevels.HasOftp2Features(writer.Level))
            writer.TextWithLength(ReasonText);
    }

    internal static SFNA Read(CommandReader reader) => new()
    {
        ReasonCode = reader.Numeric(2).ToString("00"),
        RetryLater = ParseYesNo(reader.Alpha(1)),
        ReasonText = ProtocolLevels.HasOftp2Features(reader.Level) && !reader.AtEnd ? reader.TextWithLength() : "",
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

    internal override void Write(CommandWriter writer)
    {
        // The counts are shorter before OFTP 2.0: 9 and 12 digits.
        var oftp2 = ProtocolLevels.HasOftp2Features(writer.Level);
        writer
            .Numeric(RecordCount, oftp2 ? 17 : 9)
            .Numeric(UnitCount, oftp2 ? 17 : 12);
    }

    internal static EFID Read(CommandReader reader)
    {
        var oftp2 = ProtocolLevels.HasOftp2Features(reader.Level);
        return new EFID
        {
            RecordCount = reader.Numeric(oftp2 ? 17 : 9),
            UnitCount = reader.Numeric(oftp2 ? 17 : 12),
        };
    }
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

    internal override void Write(CommandWriter writer)
    {
        writer.Numeric(int.Parse(LevelReasonCode(ReasonCode, writer.Level)), 2);

        // The reason text was added in OFTP 2.0.
        if (ProtocolLevels.HasOftp2Features(writer.Level))
            writer.TextWithLength(ReasonText);
    }

    internal static EFNA Read(CommandReader reader) => new()
    {
        ReasonCode = reader.Numeric(2).ToString("00"),
        ReasonText = ProtocolLevels.HasOftp2Features(reader.Level) && !reader.AtEnd ? reader.TextWithLength() : "",
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

    internal override void Write(CommandWriter writer)
    {
        writer.Alpha(DatasetName, 26);
        WriteTimestamp(writer, Date, Time);
        writer
            .Alpha(UserData, 8)
            .Alpha(Destination, 25)
            .Alpha(Originator, 25);

        // Hash and signature of the response were added in OFTP 2.0.
        if (ProtocolLevels.HasOftp2Features(writer.Level))
            writer.BinaryWithLength(Hash).BinaryWithLength(Signature);
    }

    internal static EERP Read(CommandReader reader)
    {
        var datasetName = reader.Alpha(26);
        var (date, time) = ReadTimestamp(reader);
        var oftp2 = ProtocolLevels.HasOftp2Features(reader.Level);

        return new EERP
        {
            DatasetName = datasetName,
            Date = date,
            Time = time,
            UserData = reader.Alpha(8),
            Destination = reader.Alpha(25),
            Originator = reader.Alpha(25),
            Hash = oftp2 && !reader.AtEnd ? reader.BinaryWithLength() : [],
            Signature = oftp2 && !reader.AtEnd ? reader.BinaryWithLength() : [],
        };
    }
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

    internal override void Write(CommandWriter writer)
    {
        writer
            .Alpha(DatasetName, 26)
            .Alpha("", 6)
            .Alpha(ProtocolLevels.Date(Date, writer.Level), 8)
            .Alpha(ProtocolLevels.Time(Time, writer.Level), 10)
            .Alpha(Destination, 25)
            .Alpha(Originator, 25)
            .Alpha(Creator, 25)
            .Numeric(int.Parse(LevelReasonCode(ReasonCode, writer.Level)), 2);

        // The reason text, the hash and the signature of the response were added in OFTP 2.0.
        if (ProtocolLevels.HasOftp2Features(writer.Level))
            writer.TextWithLength(ReasonText).BinaryWithLength(Hash).BinaryWithLength(Signature);
    }

    internal static NERP Read(CommandReader reader)
    {
        var oftp2 = ProtocolLevels.HasOftp2Features(reader.Level);

        return new NERP
        {
            DatasetName = reader.Alpha(26),
            Date = reader.Skip(6).Alpha(8),
            Time = reader.Alpha(10),
            Destination = reader.Alpha(25),
            Originator = reader.Alpha(25),
            Creator = reader.Alpha(25),
            ReasonCode = reader.Numeric(2).ToString("00"),
            ReasonText = oftp2 && !reader.AtEnd ? reader.TextWithLength() : "",
            Hash = oftp2 && !reader.AtEnd ? reader.BinaryWithLength() : [],
            Signature = oftp2 && !reader.AtEnd ? reader.BinaryWithLength() : [],
        };
    }
}
