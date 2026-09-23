namespace Oftp4Net.Core.Protocol;

/// <summary>
/// Protocol release levels announced in SSIDLEV. The level is negotiated down to the lower of the two sides
/// (RFC 5024, section 5.3.2), and it decides the layout of the commands and which features may be used.
/// </summary>
/// <remarks>
/// The layouts of levels 1 and 2 are those of ODETTE-FTP 1.3 (RFC 2204); level 4 adds the extended date and time
/// stamp and the Negative End Response, level 5 the security, compression and description fields of OFTP 2.0.
/// </remarks>
public static class ProtocolLevels
{
    /// <summary>Revision 1.2, and the level RFC 2204 announces for revision 1.3.</summary>
    public const int Oftp12 = 1;

    /// <summary>Revision 1.3.</summary>
    public const int Oftp13 = 2;

    /// <summary>Revision 1.4: extended date and time stamp, Negative End Response.</summary>
    public const int Oftp14 = 4;

    /// <summary>Revision 2.0: file and session security, file description, extended sizes and reason texts.</summary>
    public const int Oftp2 = 5;

    public static readonly IReadOnlyList<int> All = [Oftp12, Oftp13, Oftp14, Oftp2];

    public static bool IsSupported(int level) => All.Contains(level);

    /// <summary>Name of the revision as it is written in the specifications.</summary>
    public static string Name(int level) => level switch
    {
        Oftp12 => "1.2/1.3",
        Oftp13 => "1.3",
        Oftp14 => "1.4",
        Oftp2 => "2.0",
        _ => level.ToString(),
    };

    /// <summary>
    /// Virtual file date and time stamps are CCYYMMDD and HHMMSScccc from revision 1.4 on, YYMMDD and HHMMSS before.
    /// </summary>
    public static bool HasExtendedTimestamp(int level) => level >= Oftp14;

    /// <summary>The Negative End Response was added in revision 1.4.</summary>
    public static bool HasNegativeEndResponse(int level) => level >= Oftp14;

    /// <summary>
    /// File level security, secure authentication, signed end responses, the file description and the reason
    /// texts of the answers exist from OFTP 2.0 on.
    /// </summary>
    public static bool HasOftp2Features(int level) => level >= Oftp2;

    /// <summary>Converts a CCYYMMDD stamp to the format of the level.</summary>
    public static string Date(string date, int level) =>
        HasExtendedTimestamp(level) || date.Length <= 6 ? date : date[2..];

    /// <summary>Converts an HHMMSScccc stamp to the format of the level.</summary>
    public static string Time(string time, int level) =>
        HasExtendedTimestamp(level) || time.Length <= 6 ? time : time[..6];
}
