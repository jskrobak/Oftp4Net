using System.Reflection;
using System.Text;

namespace Oftp4Net.Core.Protocol.Commands;

/// <summary>
/// Base class of all OFTP2 commands (RFC 5024, section 5.3).
/// </summary>
public abstract class OftpCommand
{
    /// <summary>Command identifier, the first octet of the exchange buffer.</summary>
    public abstract char Indicator { get; }

    /// <summary>Command name as used by the RFC (SSID, SFID, ...).</summary>
    public string Name => GetType().Name;

    /// <summary>Encodes the command in the layout of the given protocol release level.</summary>
    public byte[] Encode(int level = ProtocolLevels.Oftp2)
    {
        var writer = new CommandWriter(Indicator, level);
        Write(writer);
        return writer.ToArray();
    }

    internal abstract void Write(CommandWriter writer);

    /// <summary>Decodes a command written in the layout of the given protocol release level.</summary>
    public static OftpCommand Decode(byte[] buffer, int level = ProtocolLevels.Oftp2)
    {
        if (buffer.Length == 0)
            throw new OftpDecodingException("Empty exchange buffer.");

        var reader = new CommandReader(buffer, level);
        return (char)buffer[0] switch
        {
            SSRM.Id => SSRM.Read(reader),
            SSID.Id => SSID.Read(reader),
            SFID.Id => SFID.Read(reader),
            SFPA.Id => SFPA.Read(reader),
            SFNA.Id => SFNA.Read(reader),
            DATA.Id => DATA.Read(buffer),
            CDT.Id => CDT.Read(reader),
            EFID.Id => EFID.Read(reader),
            EFPA.Id => EFPA.Read(reader),
            EFNA.Id => EFNA.Read(reader),
            ESID.Id => ESID.Read(reader),
            CD.Id => new CD(),
            EERP.Id => EERP.Read(reader),
            NERP.Id => NERP.Read(reader),
            RTR.Id => new RTR(),
            SECD.Id => new SECD(),
            AUCH.Id => AUCH.Read(reader),
            AURP.Id => AURP.Read(reader),
            var other => throw new OftpProtocolException(ReasonCodes.CommandNotRecognised,
                $"Unknown command indicator '{other}' (0x{buffer[0]:X2}).")
        };
    }

    /// <summary>
    /// Writes the reserved area and the virtual file date and time stamps, which are shorter before revision 1.4
    /// (YYMMDD and HHMMSS instead of CCYYMMDD and HHMMSScccc) while the following fields keep their positions.
    /// </summary>
    internal static void WriteTimestamp(CommandWriter writer, string date, string time)
    {
        if (ProtocolLevels.HasExtendedTimestamp(writer.Level))
            writer.Alpha("", 3)
                .Alpha(ProtocolLevels.Date(date, writer.Level), 8)
                .Alpha(ProtocolLevels.Time(time, writer.Level), 10);
        else
            writer.Alpha("", 9)
                .Alpha(ProtocolLevels.Date(date, writer.Level), 6)
                .Alpha(ProtocolLevels.Time(time, writer.Level), 6);
    }

    internal static (string Date, string Time) ReadTimestamp(CommandReader reader) =>
        ProtocolLevels.HasExtendedTimestamp(reader.Level)
            ? (reader.Skip(3).Alpha(8), reader.Alpha(10))
            : (reader.Skip(9).Alpha(6), reader.Alpha(6));

    /// <summary>
    /// Answer reason codes that the level does not know are reported as unspecified: 14 was added in revision 1.4
    /// and the codes of file level security in OFTP 2.0.
    /// </summary>
    internal static string LevelReasonCode(string reasonCode, int level)
    {
        if (ProtocolLevels.HasOftp2Features(level))
            return reasonCode;

        if (!int.TryParse(reasonCode, out var code))
            return AnswerReasonCodes.UnspecifiedReason;

        if (code > 14 || (code == 14 && !ProtocolLevels.HasNegativeEndResponse(level)))
            return AnswerReasonCodes.UnspecifiedReason;

        return reasonCode;
    }

    internal static string YesNo(bool value) => value ? "Y" : "N";

    internal static bool ParseYesNo(string value) => value == "Y";

    /// <summary>Human readable one line dump of all public properties, for logging.</summary>
    public override string ToString()
    {
        var sb = new StringBuilder(Name);
        foreach (var property in GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (property.Name == nameof(Indicator))
                continue;

            var value = property.GetValue(this);
            var text = value switch
            {
                byte[] bytes => $"<{bytes.Length} bytes>",
                string s when property.Name.Contains("Password", StringComparison.Ordinal) => new string('*', s.Length),
                _ => value?.ToString()
            };
            sb.Append(' ').Append(property.Name).Append('=').Append(text);
        }

        return sb.ToString();
    }
}
