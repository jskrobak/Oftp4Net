using System.Security.Authentication;
using Havit.Data.Patterns.UnitOfWorks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Oftp4Net.DataLayer.Repositories;
using Oftp4Net.Domain;

namespace Oftp4Net.Services;

public sealed class LoopbackSeedOptions
{
    public bool Enabled { get; set; }

    /// <summary>ODETTE code used both as SSID and SFID of the identity and of the partner.</summary>
    public string Code { get; set; } = "O0013000000LOOPBACK";

    /// <summary>Password sent in SSID (max. 8 characters).</summary>
    public string Password { get; set; } = "LOOPBACK";

    /// <summary>Not the standard 6619, to avoid conflicts with another OFTP server on the same machine.</summary>
    public int Port { get; set; } = 16619;

    /// <summary>Certificate file (as in SeedCertificates) used as the listener's server certificate.</summary>
    public string CertificatePath { get; set; } = "";
}

/// <summary>
/// Development helper: creates an identity, a TLS listener on localhost and a partner that points to that listener
/// with the same codes, so files can be sent to ourselves. Existing records are left untouched.
/// </summary>
public class LoopbackSeedService(
    IIdentityRepository identityRepository,
    IPartnerRepository partnerRepository,
    IListenerRepository listenerRepository,
    ICertificateRepository certificateRepository,
    IUnitOfWork unitOfWork,
    IConfiguration configuration,
    ILogger<LoopbackSeedService> logger)
{
    private const SslProtocols TlsVersions = SslProtocols.Tls12 | SslProtocols.Tls13;

    public async Task SeedAsync(string contentRootPath, CancellationToken cancellationToken = default)
    {
        var options = configuration.GetSection("SeedLoopback").Get<LoopbackSeedOptions>();
        if (options is not { Enabled: true })
            return;

        var certificate = await FindCertificateAsync(options, contentRootPath, cancellationToken);
        if (certificate is null)
        {
            logger.LogWarning("Loopback seed skipped: certificate {Path} is not in the database", options.CertificatePath);
            return;
        }

        var identity = await identityRepository.FindBySfidAsync(options.Code, cancellationToken);
        if (identity is null)
        {
            identity = new Identity
            {
                Name = "Loopback",
                Description = "Development identity, partner 'Loopback' is the same node",
                SSID = options.Code,
                SFID = options.Code,
                Password = options.Password,
            };
            unitOfWork.AddForInsert(identity);
        }

        if (await partnerRepository.FindBySsidAsync(options.Code, cancellationToken) is null)
        {
            unitOfWork.AddForInsert(new Partner
            {
                Name = "Loopback",
                Description = "Development partner: this server itself over TLS",
                SSID = options.Code,
                SFID = options.Code,
                Password = options.Password,
                // Not "localhost": it may resolve to ::1, where another process can listen on the same port.
                Host = "127.0.0.1",
                Port = options.Port,
                UseTls = true,
                // TLS 1.3 alone is not supported by SslStream on macOS.
                Tls = TlsVersions,
                TrustedCertificateId = certificate.Id,
            });
        }

        var listeners = await listenerRepository.GetAllAsync(cancellationToken);
        if (!listeners.Any(l => l.Port == options.Port))
        {
            unitOfWork.AddForInsert(new Listener
            {
                Name = "Loopback TLS",
                Enabled = true,
                ListenIPAddress = "127.0.0.1",
                Port = options.Port,
                UseTls = true,
                Tls = TlsVersions,
                CertificateId = certificate.Id,
                Identity = identity,
            });
        }

        await unitOfWork.CommitAsync(cancellationToken);
        logger.LogInformation("Loopback partner {Code} is configured on localhost:{Port}", options.Code, options.Port);
    }

    private async Task<Certificate?> FindCertificateAsync(LoopbackSeedOptions options, string contentRootPath,
        CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(Path.Combine(contentRootPath, options.CertificatePath));
        if (!File.Exists(path))
            return null;

        var data = Convert.ToBase64String(await File.ReadAllBytesAsync(path, cancellationToken));
        return (await certificateRepository.GetAllAsync(cancellationToken)).FirstOrDefault(c => c.Base64Data == data);
    }
}
