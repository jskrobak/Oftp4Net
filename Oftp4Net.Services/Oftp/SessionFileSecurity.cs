using System.Security.Cryptography;
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
/// Certification authorities of the Odette trust list, used to build the chain of a certificate up to its issuer.
/// </param>
/// <param name="crls">The revocation lists of the authorities; without it only the validity period is checked.</param>
public sealed class SessionFileSecurity(ICertificateRepository certificates, GlobalSettings settings,
    X509Certificate2Collection? trustAnchors = null, CrlStore? crls = null) : IDisposable
{
    private readonly Dictionary<int, X509Certificate2> _loaded = [];
    private readonly Dictionary<string, string?> _checked = [];
    private X509Certificate2Collection? _authorities;

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
    /// answer is kept for the session, so the lists are read at most once per certificate.
    /// </summary>
    public async Task<string?> CheckUsableAsync(X509Certificate2? certificate, CancellationToken cancellationToken)
    {
        if (certificate is null)
            return null;

        if (_checked.TryGetValue(certificate.Thumbprint, out var known))
            return known;

        var problem = await CheckAsync(certificate, cancellationToken);
        _checked[certificate.Thumbprint] = problem;
        return problem;
    }

    private async Task<string?> CheckAsync(X509Certificate2 certificate, CancellationToken cancellationToken)
    {
        var now = DateTime.Now;
        if (now < certificate.NotBefore || now > certificate.NotAfter)
            return $"Certificate '{certificate.Subject}' is valid from {certificate.NotBefore:g} to " +
                   $"{certificate.NotAfter:g} only.";

        if (crls is null || !settings.CheckCertificateRevocation)
            return null;

        // The lists are the ones of the authority that issued the certificate, so its chain is built first. A
        // certificate that is trusted by itself has no authority and no list.
        var issuer = await FindIssuerAsync(certificate, cancellationToken);
        if (issuer is null)
            return null;

        var verdict = await crls.CheckAsync(certificate, issuer, cancellationToken);
        return verdict.Result switch
        {
            CrlResult.Revoked => $"Certificate '{certificate.Subject}' was revoked by its issuer: {verdict.Problem}",
            CrlResult.Unknown when settings.RequireRevocationInformation =>
                $"The revocation state of '{certificate.Subject}' is unknown: {verdict.Problem}",
            _ => null,
        };
    }

    /// <summary>
    /// The certificate of the authority that issued this one, from its chain; <c>null</c> for a self signed
    /// certificate or when the authority is not known here, where there is no list to read. The chain is built
    /// with the authorities of the trust list and everything stored here, and it does not have to be trusted: an
    /// authority the administrator imported signs its revocation list just as well.
    /// </summary>
    private async Task<X509Certificate2?> FindIssuerAsync(X509Certificate2 certificate, CancellationToken cancellationToken)
    {
        if (certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData))
            return null;

        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllFlags;
        chain.ChainPolicy.ExtraStore.AddRange(await KnownAuthoritiesAsync(cancellationToken));

        chain.Build(certificate);
        return chain.ChainElements.Count > 1 ? chain.ChainElements[1].Certificate : null;
    }

    /// <summary>Everything that can complete a chain: the trust list and the certificates stored here.</summary>
    private async Task<X509Certificate2Collection> KnownAuthoritiesAsync(CancellationToken cancellationToken)
    {
        if (_authorities is not null)
            return _authorities;

        var store = new X509Certificate2Collection(trustAnchors ?? []);
        foreach (var entity in await certificates.GetAllAsync(cancellationToken))
        {
            try
            {
                store.Add(CertificateLoader.Load(entity));
            }
            catch (Exception ex) when (ex is CryptographicException or InvalidOperationException or FormatException)
            {
                // A certificate that cannot be read completes no chain.
            }
        }

        return _authorities = store;
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
