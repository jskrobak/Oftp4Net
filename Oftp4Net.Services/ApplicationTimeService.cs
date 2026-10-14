using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Havit.Services.TimeServices;

namespace Oftp4Net.Services;

// <summary>
/// Provides current time in local time-zone ("Central Europe Standard Time", "Europe/Prague" for non-Windows platforms).
/// </summary>
public partial class ApplicationTimeService : TimeZoneTimeServiceBase, ITimeService
{
    /// <summary>
    /// Returns time-zone you want to treat as local ("Central Europe Standard Time", "Europe/Prague" for non-Windows platforms).
    /// </summary>
    protected override TimeZoneInfo CurrentTimeZone => TimeZoneInfo.FindSystemTimeZoneById(
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "Central Europe Standard Time"
            : "Europe/Prague"); // MacOS

    /// <summary>
    /// Reads a time given to the application, e.g. through the REST API: with an offset or <c>Z</c> it is an instant
    /// and is converted to the local time, without one it is the local time already.
    /// </summary>
    public bool TryParseTime(string? value, out DateTime time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var text = value.Trim();
        if (OffsetPattern().IsMatch(text))
        {
            if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant))
                return false;
            time = TimeZoneInfo.ConvertTime(instant, CurrentTimeZone).DateTime;
            return true;
        }

        if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            return false;
        time = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return true;
    }

    [GeneratedRegex(@"(Z|[+-]\d{2}:?\d{2})$", RegexOptions.IgnoreCase)]
    private static partial Regex OffsetPattern();
}
