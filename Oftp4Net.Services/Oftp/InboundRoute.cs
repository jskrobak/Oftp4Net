using System.Text.RegularExpressions;

namespace Oftp4Net.Services.Oftp;

/// <summary>
/// Where a received file is stored, chosen by its virtual file name (SFIDDSN). Without a matching rule the file
/// stays in the receive directory under the code of the partner.
/// </summary>
public class InboundRoute
{
    /// <summary>
    /// Virtual file name the rule applies to, with <c>*</c> for any number of characters and <c>?</c> for one:
    /// <c>ORDERS*</c> takes everything that begins with ORDERS. Upper and lower case do not matter.
    /// </summary>
    public string Pattern { get; set; } = string.Empty;

    /// <summary>
    /// Directory the file is written to, absolute or relative to the data directory. It is created when it does
    /// not exist.
    /// </summary>
    public string Directory { get; set; } = string.Empty;
}

/// <summary>Applies the rules of <see cref="GlobalSettings.InboundRoutes"/> to a received file.</summary>
public static class InboundRoutes
{
    /// <summary>
    /// The directory of the first rule whose pattern matches, <c>null</c> when none does and the file belongs
    /// into the receive directory.
    /// </summary>
    public static string? Find(IEnumerable<InboundRoute>? routes, string virtualFileName)
    {
        var name = virtualFileName.Trim();
        foreach (var route in routes ?? [])
        {
            if (string.IsNullOrWhiteSpace(route.Pattern) || string.IsNullOrWhiteSpace(route.Directory))
                continue;

            if (Matches(route.Pattern, name))
                return route.Directory.Trim();
        }

        return null;
    }

    /// <summary>
    /// A path in <paramref name="directory"/> for a file name that is not taken yet; a number is added when it
    /// is. Nothing that arrived before is overwritten, and a file that is being received right now counts as
    /// taken as well.
    /// </summary>
    public static string UniquePath(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        for (var number = 1; Taken(path) && number < 1000; number++)
            path = Path.Combine(directory, $"{fileName}_{number}");

        return path;
    }

    // The file being received lies next to its final name with the extension .part.
    private static bool Taken(string path) => File.Exists(path) || File.Exists(path + ".part");

    /// <summary>Whether a virtual file name matches a pattern with <c>*</c> and <c>?</c>.</summary>
    public static bool Matches(string pattern, string virtualFileName)
    {
        var expression = "^" + string.Join("", pattern.Trim().Select(c => c switch
        {
            '*' => ".*",
            '?' => ".",
            _ => Regex.Escape(c.ToString()),
        })) + "$";

        try
        {
            return Regex.IsMatch(virtualFileName.Trim(), expression,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
