using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Oftp4Net.Services.Health;

/// <summary>
/// Files can be written to the receive and outbox directories and to the data protection keys, and the disks they
/// are on have space left (<c>HealthChecks:MinFreeDiskSpaceMB</c>, default 1024).
/// </summary>
public sealed class StorageHealthCheck(GlobalSettingsService settingsService, IConfiguration configuration) : IHealthCheck
{
    public const long DefaultMinFreeDiskSpaceMb = 1024;

    public sealed record DirectoryProbe(string Name, string Path, string? Error, long? FreeBytes);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetGlobalSettingsAsync();
        var keys = Path.GetFullPath(configuration["DataProtection:KeysDirectory"] ?? "keys");

        var probes = new[]
        {
            Probe("receive directory", settingsService.ResolvePath(settings.ReceiveDirectory)),
            Probe("outbox directory", settingsService.ResolvePath(settings.OutboxDirectory)),
            Probe("data protection keys", keys),
        };

        var minFree = configuration.GetValue("HealthChecks:MinFreeDiskSpaceMB", DefaultMinFreeDiskSpaceMb) * 1024 * 1024;
        return Evaluate(probes, minFree);
    }

    /// <summary>Writes and deletes a small file in the directory (creating it, as the application would) and reads the free space.</summary>
    public static DirectoryProbe Probe(string name, string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var file = System.IO.Path.Combine(path, $".health-{Guid.NewGuid():N}");
            File.WriteAllBytes(file, [0]);
            File.Delete(file);

            // On Windows the drive is its root, elsewhere any path on the file system will do.
            var drive = new DriveInfo(OperatingSystem.IsWindows() ? System.IO.Path.GetPathRoot(path)! : path);
            return new DirectoryProbe(name, path, null, drive.AvailableFreeSpace);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new DirectoryProbe(name, path, ex.Message, null);
        }
    }

    public static HealthCheckResult Evaluate(IReadOnlyCollection<DirectoryProbe> probes, long minFreeBytes)
    {
        var data = probes.ToDictionary(p => p.Name, p => (object)(p.Error is null
            ? $"{p.Path} ({FormatSize(p.FreeBytes ?? 0)} free)"
            : $"{p.Path}: {p.Error}"));

        var failed = probes.Where(p => p.Error is not null).ToList();
        if (failed.Count > 0)
            return HealthCheckResult.Unhealthy(
                string.Join(" ", failed.Select(p => $"The {p.Name} {p.Path} cannot be written: {p.Error}")), data: data);

        var full = probes.Where(p => p.FreeBytes < minFreeBytes).ToList();
        if (full.Count > 0)
            return HealthCheckResult.Degraded(
                string.Join(" ", full.Select(p => $"Only {FormatSize(p.FreeBytes ?? 0)} are free for the {p.Name} {p.Path}.")), data: data);

        return HealthCheckResult.Healthy("The directories can be written and have space left.", data);
    }

    private static string FormatSize(long bytes) => bytes >= 1024L * 1024 * 1024
        ? string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024 * 1024):F1} GB")
        : string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):F0} MB");
}
