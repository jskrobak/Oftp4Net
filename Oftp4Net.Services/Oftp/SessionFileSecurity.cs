using System.Security.Cryptography.X509Certificates;
using Oftp4Net.DataLayer.Repositories;
using Oftp4Net.Domain;
using Oftp4Net.Services.Security;
using Oftp4Net.Services.Tsl;

namespace Oftp4Net.Services.Oftp;

/// <summary>
/// Certificates and limits of file level security within one session: our own certificate comes from the global
/// settings, the partner's certificate from the partner. Loaded certificates are cached for the session.
/// </summary>
/// <param name="trustAnchors">
/// Certification authorities of the Odette trust list, used to find the revocation list of a certificate.
/// </param>
public sealed class SessionFileSecurity(ICertificateRepository certificates, GlobalSettings settings,
    X509Certificate2Collection? trustAnchors = null) : IDisposable
{
    private readonly Dictionary<int, X509Certificate2> _loaded = [];
    private readonly Dictionary<string, string?> _checked = [];

    /// <summary>Files larger than this are not signed, compressed or encrypted (everything is done in memory).</summary>
    public long MaxSecuredFileSize => (long)settings.MaxSecuredFileSizeMb * 1024 * 1024;

    public string MaxSecuredFileSizeText => $"{settings.MaxSecuredFileSizeMb} MB";

    /// <summary>
    /// Our certificate with the private key for one purpose of one of our stations: signs outgoing files and
    /// responses, decrypts incoming files and answers authentication challenges. A station that assigns none uses
    /// the certificate from the settings.
    /// </summary>
    public Task<X509Certificate2?> GetOwnCertificateAsync(Identity? identity, CertificateUsage usage,
        CancellationToken cancellationToken) =>
        GetAsync(identity?.FindCertificate(null, usage)?.CertificateId ?? settings.FileSecurityCertificateId, cancellationToken);

    /// <summary>The partner's certificate: encrypts files for it and verifies its signatures.</summary>
    public Task<X509Certificate2?> GetPartnerCertificateAsync(StationSettings station, CertificateUsage usage,
        CancellationToken cancellationToken) =>
        GetAsync(station.CertificateFor(usage), cancellationToken);

    /// <summary>The partner's certificate before it was replaced, still accepted for signatures made before.</summary>
    public Task<X509Certificate2?> GetPreviousPartnerCertificateAsync(StationSettings station, CertificateUsage usage,
        CancellationToken cancellationToken) =>
        GetAsync(station.PreviousCertificateFor(usage), cancellationToken);

    /// <summary>What is applied to files sent to <paramref name="partner"/> (or one of its sub-stations).</summary>
    public async Task<FileSecuritySettings> ForSendingAsync(Partner partner, StationSettings station, Identity? identity,
        CancellationToken cancellationToken)
    {
        var suite = CipherSuite.Get(station.FileCipherSuite)
            ?? throw new FileSecurityException($"Cipher suite '{station.FileCipherSuite}' of partner {partner.Name} is not supported.");

        return new FileSecuritySettings
        {
            Sign = station.SignFiles,
            Compress = station.CompressFiles,
            Encrypt = station.EncryptFiles,
            Suite = suite,
            SigningCertificate = station.SignFiles
                ? await GetOwnCertificateAsync(identity, CertificateUsage.FileSignature, cancellationToken)
                : null,
            EncryptionCertificate = station.EncryptFiles
                ? await GetPartnerCertificateAsync(station, CertificateUsage.FileEncryption, cancellationToken)
                : null,
        };
    }

    /// <summary>
    /// Why a certificate must not be used for file level security any more: it expired, it is not valid yet, or
    /// its issuer put it on a revocation list (Odette OP08 2.6 and 1.10). <c>null</c> when it can be used. The
    /// answer is kept for the session, the revocation lists are read at most once per certificate.
    /// </summary>
    public string? CheckUsable(X509Certificate2? certificate)
    {
        if (certificate is null)
            return null;

        if (_checked.TryGetValue(certificate.Thumbprint, out var known))
            return known;

        var problem = Check(certificate);
        _checked[certificate.Thumbprint] = problem;
        return problem;
    }

    private string? Check(X509Certificate2 certificate)
    {
        var now = DateTime.Now;
        if (now < certificate.NotBefore || now > certificate.NotAfter)
            return $"Certificate '{certificate.Subject}' is valid from {certificate.NotBefore:g} to " +
                   $"{certificate.NotAfter:g} only.";

        return CertificateTrust.IsRevoked(certificate, [], trustAnchors ?? [], settings.RevocationPolicy, out var problem)
            ? $"Certificate '{certificate.Subject}' was revoked by its issuer: {problem}"
            : null;
    }

    private async Task<X509Certificate2?> GetAsync(int? id, CancellationToken cancellationToken)
    {
        if (id is not { } certificateId)
            return null;

        if (_loaded.TryGetValue(certificateId, out var loaded))
            return loaded;

        var certificate = CertificateLoader.Load(await certificates.GetObjectAsync(certificateId, cancellationToken));
        _loaded.Add(certificateId, certificate);
        return certificate;
    }

    public void Dispose()
    {
        foreach (var certificate in _loaded.Values)
            certificate.Dispose();
        _loaded.Clear();
    }
}
