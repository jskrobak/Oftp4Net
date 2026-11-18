using System.Globalization;

namespace Oftp4Net.Services;

/// <summary>
/// A local time of the application typed as text, e.g. <c>2026-12-03 10:00</c> or <c>3.12.2026 10:00</c>. Used instead
/// of the date and time input of the browser, whose value did not always reach the server.
/// </summary>
public static class LocalTimeText
{
    public const string Format = "yyyy-MM-dd HH:mm";

    private static readonly string[] Formats =
    [
        "yyyy-MM-dd HH:mm", "yyyy-MM-dd H:mm", "yyyy-MM-ddTHH:mm", "yyyy-MM-dd",
        "d.M.yyyy H:mm", "d. M. yyyy H:mm", "d.M.yyyy", "d. M. yyyy",
    ];

    public static string ToText(DateTime time) => time.ToString(Format, CultureInfo.InvariantCulture);

    /// <summary>Reads the text; a date without a time is midnight.</summary>
    public static bool TryParse(string? text, out DateTime time) =>
        DateTime.TryParseExact((text ?? "").Trim(), Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
}
