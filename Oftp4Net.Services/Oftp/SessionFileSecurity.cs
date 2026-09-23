using System.Security.Cryptography.X509Certificates;
using Oftp4Net.DataLayer.Repositories;
using Oftp4Net.Domain;
using Oftp4Net.Services.Security;

namespace Oftp4Net.Services.Oftp;

/// <summary>
/// Certificates and limits of file level security within one session: our own certificate comes from the global
/// settings, the partner's certificate from the partner. Loaded certificates are cached for the session.
/// </summary>
public sealed class SessionFileSecurity(ICertificateRepository certificates, GlobalSettings settings) : IDisposable
{
    private readonly Dictionary<int, X509Certificate2> _loaded = [];

    /// <summary>Files larger than this are not signed, compressed or encrypted (everything is done in memory).</summary>
    public long MaxSecuredFileSize => (long)settings.MaxSecuredFileSizeMb * 1024 * 1024;

    public string MaxSecuredFileSizeText => $"{settings.MaxSecuredFileSizeMb} MB";

    /// <summary>Our certificate with the private key: signs outgoing files and responses, decrypts incoming files.</summary>
    public Task<X509Certificate2?> GetOwnCertificateAsync(CancellationToken cancellationToken) =>
        GetAsync(settings.FileSecurityCertificateId, cancellationToken);

    /// <summary>The partner's certificate: encrypts files for it and verifies its signatures.</summary>
    public Task<X509Certificate2?> GetPartnerCertificateAsync(Partner partner, CancellationToken cancellationToken) =>
        GetAsync(partner.SecurityCertificateId, cancellationToken);

    /// <summary>The partner's certificate before it was replaced, still accepted for signatures made before.</summary>
    public Task<X509Certificate2?> GetPreviousPartnerCertificateAsync(Partner partner, CancellationToken cancellationToken) =>
        GetAsync(partner.PreviousSecurityCertificateId, cancellationToken);

    /// <summary>What is applied to files sent to <paramref name="partner"/> (or one of its sub-stations).</summary>
    public async Task<FileSecuritySettings> ForSendingAsync(Partner partner, StationSettings station, CancellationToken cancellationToken)
    {
        var suite = CipherSuite.Get(station.FileCipherSuite)
            ?? throw new FileSecurityException($"Cipher suite '{station.FileCipherSuite}' of partner {partner.Name} is not supported.");

        return new FileSecuritySettings
        {
            Sign = station.SignFiles,
            Compress = station.CompressFiles,
            Encrypt = station.EncryptFiles,
            Suite = suite,
            SigningCertificate = station.SignFiles ? await GetOwnCertificateAsync(cancellationToken) : null,
            EncryptionCertificate = station.EncryptFiles ? await GetPartnerCertificateAsync(partner, cancellationToken) : null,
        };
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
