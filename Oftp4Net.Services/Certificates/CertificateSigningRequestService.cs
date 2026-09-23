using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Havit.Data.Patterns.UnitOfWorks;
using Microsoft.Extensions.Logging;
using Oftp4Net.DataLayer.Repositories;
using Oftp4Net.Domain;
using Oftp4Net.Services.Oftp;

namespace Oftp4Net.Services.Certificates;

/// <summary>
/// Certificate signing requests for a certification authority (e.g. the Odette CA): the key pair is created and kept
/// here, the request (CSR) is sent to the CA, and the certificate the CA returns is stored together with the key as a
/// certificate with a private key, usable for TLS and file security.
/// </summary>
public class CertificateSigningRequestService(
    ICertificateSigningRequestRepository requests,
    IIdentityRepository identities,
    IUnitOfWork unitOfWork,
    GlobalSettingsService settingsService,
    ILogger<CertificateSigningRequestService> logger)
{
    /// <summary>Largest file accepted as a signed certificate (a certificate with its chain has a few kilobytes).</summary>
    public const int MaxCertificateFileSize = 256 * 1024;

    /// <summary>Values of a new request taken from the station profile and the first identity.</summary>
    public async Task<CsrOptions> GetDefaultsAsync(CancellationToken cancellationToken = default)
    {
        var profile = (await settingsService.GetGlobalSettingsAsync()).StationProfile;
        var identity = (await identities.GetAllAsync(cancellationToken)).OrderBy(i => i.Id).FirstOrDefault();

        return new CsrOptions
        {
            CommonName = profile.PublicHost ?? "",
            Organization = profile.CompanyName,
            Locality = profile.City,
            Country = profile.Country,
            OdetteId = identity?.SSID.Trim(),
            Email = profile.Contacts.SelectMany(c => c.Emails).FirstOrDefault(),
        };
    }

    public async Task<CertificateSigningRequest> CreateAsync(string name, CsrOptions options, CancellationToken cancellationToken = default)
    {
        var (csr, key) = CsrBuilder.Create(options);
        using (key)
        {
            var request = new CertificateSigningRequest
            {
                Created = DateTime.Now,
                Name = string.IsNullOrWhiteSpace(name) ? $"{options.CommonName.Trim()} {DateTime.Now:yyyy-MM-dd}" : name.Trim(),
                Subject = CsrBuilder.SubjectName(options).Name,
                KeySize = options.KeySize,
                Csr = csr,
                PrivateKey = Convert.ToBase64String(key.ExportPkcs8PrivateKey()),
            };

            unitOfWork.AddForInsert(request);
            await unitOfWork.CommitAsync(cancellationToken);

            logger.LogInformation("Certificate signing request {Name} created for {Subject} ({KeySize} bit RSA)",
                request.Name, request.Subject, request.KeySize);
            return request;
        }
    }

    /// <summary>
    /// Stores the certificate signed by the CA together with the private key of the request (and the CA certificates
    /// found in the file) as a certificate with a private key. The key is removed from the request.
    /// </summary>
    public async Task<Certificate> CompleteAsync(int requestId, byte[] signedFile, CancellationToken cancellationToken = default)
    {
        var request = await requests.GetObjectAsync(requestId, cancellationToken);
        if (request.CompletedDate is not null || string.IsNullOrEmpty(request.PrivateKey))
            throw new InvalidOperationException("The request has already been completed.");

        using var key = RSA.Create();
        key.ImportPkcs8PrivateKey(Convert.FromBase64String(request.PrivateKey), out _);

        var (signed, chain) = CsrBuilder.ReadSigned(signedFile, key);
        using var withKey = signed.CopyWithPrivateKey(key);

        // The password protects the PKCS#12 in the database (encrypted there as well); it never leaves the server.
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        var bundle = new X509Certificate2Collection(withKey);
        bundle.AddRange(chain);
        var pfx = bundle.Export(X509ContentType.Pkcs12, password)
                  ?? throw new CryptographicException("The certificate could not be stored with its key.");

        var certificate = CertificateLoader.CreateEntity(pfx, request.Name + ".pfx", password);
        certificate.Name = request.Name;
        unitOfWork.AddForInsert(certificate);

        request.Certificate = certificate;
        request.CompletedDate = DateTime.Now;
        request.PrivateKey = null;
        unitOfWork.AddForUpdate(request);

        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation("Certificate {Subject} issued by {Issuer}, valid to {ValidTo}, stored for request {Name}",
            signed.Subject, signed.Issuer, signed.NotAfter, request.Name);
        return certificate;
    }

    /// <summary>Deletes a request; an open one loses its private key, so a certificate for it can no longer be used.</summary>
    public async Task DeleteAsync(int requestId, CancellationToken cancellationToken = default)
    {
        var request = await requests.GetObjectAsync(requestId, cancellationToken);
        unitOfWork.AddForDelete(request);
        await unitOfWork.CommitAsync(cancellationToken);
    }
}
