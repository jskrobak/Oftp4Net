using Microsoft.AspNetCore.Http;

namespace Oftp4Net.Services;

/// <summary>
/// Stores files uploaded to the send queue in <see cref="GlobalSettings.OutboxDirectory"/>.
/// </summary>
public class OutboxStorage(GlobalSettingsService settingsService)
{
    /// <summary>Saves the uploaded file and returns its full path.</summary>
    public async Task<string> SaveAsync(IFormFile file, CancellationToken cancellationToken = default)
    {
        var directory = await GetDirectoryAsync();
        Directory.CreateDirectory(directory);

        // A unique prefix keeps files with the same name apart; the original name stays readable.
        var path = Path.Combine(directory, $"{DateTime.Now:yyyyMMddHHmmss}_{Guid.NewGuid():N}_{SafeFileName(file.FileName)}");

        await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await file.CopyToAsync(target, cancellationToken);
        return path;
    }

    /// <summary>Deletes the file if it was stored in the outbox (files elsewhere on the server are left alone).</summary>
    public async Task DeleteIfInOutboxAsync(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return;

        var directory = Path.GetFullPath(await GetDirectoryAsync()) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(filePath);
        if (fullPath.StartsWith(directory, StringComparison.Ordinal) && File.Exists(fullPath))
            File.Delete(fullPath);
    }

    /// <summary>Suggests a virtual file name (max. 26 characters, upper case) from an original file name.</summary>
    public static string SuggestVirtualFileName(string fileName)
    {
        var chars = Path.GetFileName(fileName).ToUpperInvariant()
            .Select(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '-' or '_' ? c : '_')
            .ToArray();
        var name = new string(chars);
        return name.Length <= 26 ? name : name[..26];
    }

    private async Task<string> GetDirectoryAsync() =>
        settingsService.ResolvePath((await settingsService.GetGlobalSettingsAsync()).OutboxDirectory);

    private static string SafeFileName(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string(Path.GetFileName(fileName).Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return name.Length == 0 ? "file" : name;
    }
}
