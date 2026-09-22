using Havit.Data.Patterns.UnitOfWorks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Oftp4Net.DataLayer.Repositories;
using Oftp4Net.Services.Oftp;

namespace Oftp4Net.Services;

public sealed class CertificateSeedOptions
{
    /// <summary>Path of the certificate file, relative paths are resolved against the application content root.</summary>
    public string Path { get; set; } = "";
    public string? Password { get; set; }
}

/// <summary>
/// Imports certificates listed in the <c>SeedCertificates</c> configuration section (e.g. the development TLS
/// certificate) into the database, unless they are already there.
/// </summary>
public class CertificateSeedService(
    ICertificateRepository certificateRepository,
    IUnitOfWork unitOfWork,
    IConfiguration configuration,
    ILogger<CertificateSeedService> logger)
{
    public async Task SeedAsync(string contentRootPath, CancellationToken cancellationToken = default)
    {
        var seeds = configuration.GetSection("SeedCertificates").Get<List<CertificateSeedOptions>>() ?? [];
        if (seeds.Count == 0)
            return;

        var existing = (await certificateRepository.GetAllAsync(cancellationToken))
            .Select(c => c.Base64Data)
            .ToHashSet();

        foreach (var seed in seeds)
        {
            var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(contentRootPath, seed.Path));
            if (!File.Exists(path))
            {
                logger.LogWarning("Seed certificate {Path} not found", path);
                continue;
            }

            var entity = CertificateLoader.CreateEntity(await File.ReadAllBytesAsync(path, cancellationToken), path, seed.Password);
            if (existing.Contains(entity.Base64Data))
                continue;

            unitOfWork.AddForInsert(entity);
            existing.Add(entity.Base64Data);
            logger.LogInformation("Imported certificate {Name} from {Path}", entity.Name, path);
        }

        await unitOfWork.CommitAsync(cancellationToken);
    }
}
