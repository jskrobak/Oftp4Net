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

    public byte[] Encode()
    {
        var writer = new CommandWriter(Indicator);
        Write(writer);
        return writer.ToArray();
    }

    internal abstract void Write(CommandWriter writer);

    public static OftpCommand Decode(byte[] buffer)
    {
        if (buffer.Length == 0)
            throw new OftpDecodingException("Empty exchange buffer.");

        var reader = new CommandReader(buffer);
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
