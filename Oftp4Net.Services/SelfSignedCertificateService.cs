using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Havit.Data.Patterns.UnitOfWorks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Oftp4Net.DataLayer.Repositories;
using Oftp4Net.Domain;

namespace Oftp4Net.Services;

public sealed class SelfSignedCertificateOptions
{
    /// <summary>Create a certificate on startup when the database contains none with a private key.</summary>
    public bool GenerateCertificate { get; set; } = true;

    /// <summary>Host name put into the certificate (CN and SAN). Defaults to the machine (container) name.</summary>
    public string? CertificateSubject { get; set; }

    public int CertificateValidityDays { get; set; } = 3 * 365;
}

/// <summary>
/// Creates a self-signed TLS certificate for this installation on first start, so that every installation
/// (e.g. every Docker container) has its own key. The public certificate is exported for partners.
/// </summary>
public class SelfSignedCertificateService(
    ICertificateRepository certificateRepository,
    IUnitOfWork unitOfWork,
    GlobalSettingsService settingsService,
    IConfiguration configuration,
    ILogger<SelfSignedCertificateService> logger)
{
    public const string ExportFileName = "server.crt";

    public async Task EnsureCertificateAsync(CancellationToken cancellationToken = default)
    {
        var options = configuration.GetSection("Tls").Get<SelfSignedCertificateOptions>() ?? new SelfSignedCertificateOptions();
        if (!options.GenerateCertificate)
            return;

        var certificates = await certificateRepository.GetAllAsync(cancellationToken);
        if (certificates.Any(c => c.HasPrivateKey))
            return;

        var subject = string.IsNullOrWhiteSpace(options.CertificateSubject)
            ? Environment.MachineName.ToLowerInvariant()
            : options.CertificateSubject.Trim();

        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        using var certificate = Create(subject, TimeSpan.FromDays(options.CertificateValidityDays));
        var pfx = certificate.Export(X509ContentType.Pfx, password);

        var entity = new Certificate
        {
            Name = certificate.Subject,
            HasPrivateKey = true,
            ValidFrom = certificate.NotBefore,
            ValidTo = certificate.NotAfter,
            Base64Data = Convert.ToBase64String(pfx),
            Password = password,
        };
        unitOfWork.AddForInsert(entity);
        await unitOfWork.CommitAsync(cancellationToken);

        // Present it to partners that require client authentication, unless another certificate is configured.
        var settings = await settingsService.GetGlobalSettingsAsync();
        if (settings.OftpClientCertificateId is null)
        {
            settings.OftpClientCertificateId = entity.Id;
            await settingsService.SetGlobalSettingsAsync(settings);
        }

        var exportPath = await ExportPublicCertificateAsync(certificate, cancellationToken);

        logger.LogWarning(
            "Created self-signed TLS certificate {Subject} (SHA-256 {Thumbprint}), valid to {ValidTo:d}. " +
            "Select it as the server certificate of a listener and give {ExportPath} to your partners.",
            certificate.Subject, certificate.GetCertHashString(HashAlgorithmName.SHA256), certificate.NotAfter, exportPath);
    }

    /// <summary>Creates a self-signed certificate usable for TLS server and client authentication.</summary>
    public static X509Certificate2 Create(string subject, TimeSpan validity)
    {
        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest(
            new X500DistinguishedName($"CN={subject}, O=Oftp4Net"), rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2")], false)); // server and client authentication
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var san = new SubjectAlternativeNameBuilder();
        if (IPAddress.TryParse(subject, out var address))
            san.AddIpAddress(address);
        else
            san.AddDnsName(subject);
        request.CertificateExtensions.Add(san.Build());

        var now = DateTimeOffset.UtcNow;
        return request.CreateSelfSigned(now.AddDays(-1), now.Add(validity));
    }

    private async Task<string?> ExportPublicCertificateAsync(X509Certificate2 certificate, CancellationToken cancellationToken)
    {
        try
        {
            var directory = settingsService.ResolvePath("certs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, ExportFileName);
            await File.WriteAllTextAsync(path, certificate.ExportCertificatePem() + Environment.NewLine, cancellationToken);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Exporting the public certificate failed");
            return null;
        }
    }
}
