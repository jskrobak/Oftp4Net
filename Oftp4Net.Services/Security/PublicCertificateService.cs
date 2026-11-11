using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Havit.Services.TimeServices;
using Oftp4Net.DataLayer.Repositories;
using Oftp4Net.Domain;
using Oftp4Net.Services.Oftp;
using Oftp4Net.Services.Tsl;

namespace Oftp4Net.Services.Security;

/// <summary>One of our certificates as the public certificate page shows it.</summary>
/// <param name="Key">Part of its download address: the beginning of its SHA-256 fingerprint.</param>
/// <param name="UsedFor">What it is used for, without the names of identities or listeners.</param>
/// <param name="UsedFrom">For a certificate scheduled to replace one in use: when it is used from.</param>
/// <param name="Replaces">For a scheduled one: the key of the certificate it replaces, when that one is published.</param>
public sealed record PublishedCertificate(
    string Key, string Subject, string Issuer, string SerialNumber, string Sha256, string Sha1,
    DateTime ValidFrom, DateTime ValidTo, IReadOnlyList<string> UsedFor, DateTime? UsedFrom, string? Replaces);

/// <summary>What the public certificate page shows.</summary>
public sealed record PublishedCertificates(
    string? Company, string? Host, int? Port, IReadOnlyList<string> OdetteIds,
    IReadOnlyList<PublishedCertificate> Current, IReadOnlyList<PublishedCertificate> Next);

/// <summary>
/// Our certificates published without sign in (<see cref="GlobalSettings.PublishCertificates"/>): the ones in use
/// for file security and TLS and the ones scheduled to replace them. Nothing else is reachable through it: not the
/// certificates of partners, not authorities, not the previous certificate of a roll-over, never a private key.
/// </summary>
public class PublicCertificateService(
    GlobalSettingsService settingsService,
    ICertificateRepository certificateRepository,
    IIdentityRepository identityRepository,
    IListenerRepository listenerRepository,
    ICertificateChangeRepository changeRepository,
    TslService tsl,
    ITimeService timeService)
{
    /// <summary>A use of one of our certificates, found by <see cref="Collect"/>.</summary>
    public sealed record Use(int CertificateId, string UsedFor, DateTime? UsedFrom = null, int? Replaces = null);

    /// <summary>The page, <c>null</c> when publishing is switched off.</summary>
    public async Task<PublishedCertificates?> GetAsync(CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetGlobalSettingsAsync();
        if (!settings.PublishCertificates)
            return null;

        var (uses, certificates) = await LoadAsync(settings, cancellationToken);
        var identities = await identityRepository.GetAllAsync(cancellationToken);
        var odetteIds = identities.Where(i => settings.PublishedIdentityIds.Contains(i.Id))
            .Select(i => i.SSID.Trim()).Where(s => s.Length > 0).Distinct().Order().ToList();

        var keys = certificates.ToDictionary(c => c.Key, c => Key(c.Value));
        List<PublishedCertificate> Describe(IEnumerable<Use> selected) => selected
            .GroupBy(u => u.CertificateId)
            .Where(g => certificates.ContainsKey(g.Key))
            .Select(g => Published(certificates[g.Key], g.Select(u => u.UsedFor).Distinct().ToList(), g.First().UsedFrom,
                g.First().Replaces is { } replaced && keys.TryGetValue(replaced, out var key) ? key : null))
            .ToList();

        var current = Describe(uses.Where(u => u.UsedFrom is null));
        var next = Describe(uses.Where(u => u.UsedFrom is not null)).OrderBy(c => c.UsedFrom).ToList();
        foreach (var certificate in certificates.Values)
            certificate.Dispose();

        var profile = settings.StationProfile;
        return new PublishedCertificates(profile.CompanyName, profile.PublicHost, profile.PublicPort, odetteIds,
            current.OrderBy(c => c.Subject).ToList(), next);
    }

    /// <summary>
    /// One of the published certificates by its key, with the chain up to its root when <paramref name="chain"/>;
    /// <c>null</c> when publishing is switched off or no published certificate has that key.
    /// </summary>
    public async Task<(X509Certificate2 Certificate, X509Certificate2Collection Chain)?> FindAsync(string key, bool chain,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetGlobalSettingsAsync();
        if (!settings.PublishCertificates || key.Length < KeyLength)
            return null;

        var (_, certificates) = await LoadAsync(settings, cancellationToken);
        X509Certificate2? found = null;
        foreach (var certificate in certificates.Values)
        {
            if (found is null && string.Equals(Key(certificate), key, StringComparison.OrdinalIgnoreCase))
                found = X509CertificateLoader.LoadCertificate(certificate.RawData);
            certificate.Dispose();
        }

        if (found is null)
            return null;

        return (found, chain ? await ChainAsync(found, cancellationToken) : []);
    }

    /// <summary>
    /// Where our certificates are used now and which are scheduled to replace them. The previous certificate of a
    /// roll-over, kept only to decrypt, is not among them.
    /// </summary>
    public static List<Use> Collect(GlobalSettings settings, IEnumerable<Identity> identities, IEnumerable<Listener> listeners,
        IEnumerable<CertificateChange> scheduled)
    {
        var uses = new List<Use>();
        if (settings.FileSecurityCertificateId is { } security)
            uses.Add(new Use(security, "file signing and encryption"));
        if (settings.OftpClientCertificateId is { } client)
            uses.Add(new Use(client, "TLS client"));
        foreach (var assignment in identities.SelectMany(i => i.Certificates))
            uses.Add(new Use(assignment.CertificateId, assignment.Usage switch
            {
                CertificateUsage.FileSignature => "file signing",
                CertificateUsage.FileEncryption => "file encryption",
                CertificateUsage.EndResponse => "signing End to End Responses",
                CertificateUsage.Authentication => "secure authentication",
                _ => assignment.Usage.ToString(),
            }));
        foreach (var listener in listeners.Where(l => l is { Enabled: true, UseTls: true, CertificateId: not null }))
            uses.Add(new Use(listener.CertificateId!.Value, $"TLS server (port {listener.Port})"));
        foreach (var change in scheduled.Where(c => c is { Target: CertificateChangeTarget.Own, Status: CertificateChangeStatus.Scheduled, CertificateId: not null }))
            uses.Add(new Use(change.CertificateId!.Value, CertificateChangeService.DescribeOwn(change.Usage), change.ActivateAt,
                change.ReplacedCertificateId));
        return uses;
    }

    /// <summary>The beginning of the SHA-256 fingerprint, enough to tell our certificates apart in an address.</summary>
    public static string Key(X509Certificate2 certificate) =>
        Convert.ToHexStringLower(SHA256.HashData(certificate.RawData))[..KeyLength];

    private const int KeyLength = 16;

    private async Task<(List<Use> Uses, Dictionary<int, X509Certificate2> Certificates)> LoadAsync(GlobalSettings settings,
        CancellationToken cancellationToken)
    {
        var uses = Collect(settings,
            await identityRepository.GetAllAsync(cancellationToken),
            await listenerRepository.GetAllAsync(cancellationToken),
            await changeRepository.GetScheduledAsync(cancellationToken));

        var now = timeService.GetCurrentTime();
        var certificates = new Dictionary<int, X509Certificate2>();
        foreach (var entity in await certificateRepository.GetAllAsync(cancellationToken))
        {
            // Only our own certificates; a public one in a setting would be a mistake, not something to publish.
            if (!entity.HasPrivateKey || uses.All(u => u.CertificateId != entity.Id))
                continue;
            try
            {
                using var loaded = CertificateLoader.Load(entity);
                var certificate = X509CertificateLoader.LoadCertificate(loaded.RawData);
                // An expired certificate is of no use to a partner any more.
                if (certificate.NotAfter < now)
                    certificate.Dispose();
                else
                    certificates[entity.Id] = certificate;
            }
            catch (Exception ex) when (ex is CryptographicException or InvalidOperationException or FormatException)
            {
                // A certificate that cannot be read is not published.
            }
        }

        return (uses.Where(u => certificates.ContainsKey(u.CertificateId)).ToList(), certificates);
    }

    /// <summary>The authorities above the certificate, from the trust list and the certificates stored here.</summary>
    private async Task<X509Certificate2Collection> ChainAsync(X509Certificate2 certificate, CancellationToken cancellationToken)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllFlags;
        chain.ChainPolicy.ExtraStore.AddRange(tsl.TrustAnchors);
        foreach (var entity in await certificateRepository.GetAllAsync(cancellationToken))
        {
            if (entity.HasPrivateKey)
                continue;
            try
            {
                chain.ChainPolicy.ExtraStore.Add(CertificateLoader.Load(entity));
            }
            catch (Exception ex) when (ex is CryptographicException or InvalidOperationException or FormatException)
            {
            }
        }

        chain.Build(certificate);
        var result = new X509Certificate2Collection();
        foreach (var element in chain.ChainElements.Skip(1))
            result.Add(X509CertificateLoader.LoadCertificate(element.Certificate.RawData));
        return result;
    }

    private static PublishedCertificate Published(X509Certificate2 certificate, IReadOnlyList<string> usedFor, DateTime? usedFrom,
        string? replaces) => new(
        Key(certificate),
        certificate.Subject,
        certificate.Issuer,
        certificate.SerialNumber,
        Fingerprint(SHA256.HashData(certificate.RawData)),
        Fingerprint(SHA1.HashData(certificate.RawData)),
        certificate.NotBefore,
        certificate.NotAfter,
        usedFor,
        usedFrom,
        replaces);

    private static string Fingerprint(byte[] hash) => string.Join(':', hash.Select(b => b.ToString("X2")));
}
