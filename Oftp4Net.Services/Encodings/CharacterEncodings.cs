using System.Collections.Concurrent;
using System.Text;

namespace Oftp4Net.Services.Encodings;

/// <summary>A single byte code page offered for the ANSI or the EBCDIC side of a conversion.</summary>
/// <param name="CodePage">Code page number passed to <see cref="Encoding.GetEncoding(int)"/>.</param>
/// <param name="Name">Description shown in the user interface.</param>
public sealed record CodePageInfo(int CodePage, string Name);

/// <summary>
/// Translation tables converting the content of virtual files between a single byte ANSI (or ISO) code page
/// and an EBCDIC code page. Both sides are single byte, so the conversion maps octet to octet and does not
/// change the length of the file.
/// </summary>
public static class CharacterEncodings
{
    public const int DefaultAnsiCodePage = 1252;
    public const int DefaultEbcdicCodePage = 500;

    /// <summary>Octet used for characters the target code page does not contain ('?' in ASCII and in EBCDIC).</summary>
    private const string Substitute = "?";

    /// <summary>
    /// EBCDIC new line. The code page tables map it to U+0085 (NEL), which most ANSI code pages do not contain;
    /// text arriving from a mainframe is expected to end its lines with a line feed instead.
    /// </summary>
    private const byte EbcdicNewLine = 0x15;
    private const byte AsciiLineFeed = 0x0A;

    private static readonly ConcurrentDictionary<(int From, int To, bool ToAnsi), byte[]> Tables = new();

    static CharacterEncodings()
    {
        // EBCDIC and the ANSI code pages are not part of the default set of encodings. Registering the provider
        // repeatedly (the host does it as well) is harmless.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>Code pages offered for files stored by this server.</summary>
    public static IReadOnlyList<CodePageInfo> AnsiCodePages { get; } =
    [
        new(1252, "Windows-1252 - Western European"),
        new(1250, "Windows-1250 - Central European"),
        new(28591, "ISO-8859-1 - Latin 1"),
        new(28592, "ISO-8859-2 - Latin 2"),
        new(28605, "ISO-8859-15 - Latin 9"),
        new(850, "IBM850 - Western European (DOS)"),
        new(852, "IBM852 - Central European (DOS)"),
    ];

    /// <summary>EBCDIC code pages offered for partners.</summary>
    public static IReadOnlyList<CodePageInfo> EbcdicCodePages { get; } =
    [
        new(500, "IBM500 - International"),
        new(1148, "IBM1148 - International with Euro"),
        new(37, "IBM037 - US / Canada"),
        new(1140, "IBM1140 - US / Canada with Euro"),
        new(870, "IBM870 - Multilingual Latin 2"),
        new(20273, "IBM273 - Germany"),
        new(1141, "IBM1141 - Germany with Euro"),
        new(20277, "IBM277 - Denmark / Norway"),
        new(20284, "IBM284 - Spain"),
        new(20285, "IBM285 - United Kingdom"),
        new(20297, "IBM297 - France"),
        new(20871, "IBM871 - Iceland"),
        new(1026, "IBM1026 - Turkish Latin 5"),
        new(1047, "IBM1047 - Latin 1 (Open Systems)"),
    ];

    /// <summary>Table converting content stored in <paramref name="ansiCodePage"/> to <paramref name="ebcdicCodePage"/>.</summary>
    public static byte[] GetAnsiToEbcdicTable(int ansiCodePage, int ebcdicCodePage) =>
        Tables.GetOrAdd((ansiCodePage, ebcdicCodePage, false), key => BuildTable(key.From, key.To, toAnsi: false));

    /// <summary>Table converting content received in <paramref name="ebcdicCodePage"/> to <paramref name="ansiCodePage"/>.</summary>
    public static byte[] GetEbcdicToAnsiTable(int ebcdicCodePage, int ansiCodePage) =>
        Tables.GetOrAdd((ebcdicCodePage, ansiCodePage, true), key => BuildTable(key.From, key.To, toAnsi: true));

    private static byte[] BuildTable(int fromCodePage, int toCodePage, bool toAnsi)
    {
        var from = GetSingleByteEncoding(fromCodePage);
        var to = GetSingleByteEncoding(toCodePage);
        var substitute = to.GetBytes(Substitute)[0];

        var table = new byte[256];
        var source = new byte[1];
        for (var value = 0; value < 256; value++)
        {
            source[0] = (byte)value;
            var converted = to.GetBytes(from.GetString(source));
            table[value] = converted.Length == 1 ? converted[0] : substitute;
        }

        if (toAnsi)
            table[EbcdicNewLine] = AsciiLineFeed;

        return table;
    }

    private static Encoding GetSingleByteEncoding(int codePage)
    {
        Encoding encoding;
        try
        {
            // Characters missing in the target code page are replaced instead of throwing.
            encoding = Encoding.GetEncoding(codePage, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            throw new NotSupportedException($"Code page {codePage} is not available.", ex);
        }

        if (!encoding.IsSingleByte)
            throw new NotSupportedException($"Code page {codePage} ({encoding.WebName}) is not a single byte encoding.");

        return encoding;
    }
}
