using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Oftp4Net.Core.Protocol;
using Oftp4Net.Domain;
using Oftp4Net.Services.Security;

namespace Oftp4Net.Services.Pdx;

/// <summary>What our own datasheet is made of.</summary>
public sealed record OwnStation
{
    /// <summary>The identity described; its SSID code is the station.</summary>
    public required Identity Identity { get; init; }

    /// <summary>Further identities with the same SSID and their own SFID, published as sub-stations.</summary>
    public IReadOnlyList<Identity> SubStations { get; init; } = [];

    public required StationProfile Profile { get; init; }

    /// <summary>The listener partners call; without one the datasheet says nothing about calling us.</summary>
    public Listener? Listener { get; init; }

    public X509Certificate2? TlsCertificate { get; init; }
    public X509Certificate2? FileCertificate { get; init; }
    public X509Certificate2? ClientCertificate { get; init; }

    /// <summary>Certification authorities (Odette TSL) the chains of our certificates are completed from.</summary>
    public X509Certificate2Collection TrustAnchors { get; init; } = [];

    public DateTimeOffset Now { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? ValidFrom { get; init; }
}

/// <summary>
/// Builds our own OFTP2 Communication Setup (PDX) from an identity, the station profile and our certificates.
/// Features that need a certificate we do not have are published as forbidden, and the reason is reported.
/// </summary>
public static class PdxDatasheetBuilder
{
    private const string TlsName = "tls";
    private const string FileName = "file";
    private const string ClientName = "client";

    public static (PdxDocument Document, IReadOnlyList<string> Warnings) Build(OwnStation station)
    {
        var warnings = new List<string>();
        var profile = station.Profile;
        var identity = station.Identity;
        var certificates = new List<PdxCertificate>();

        string? Reference(X509Certificate2? certificate, string name)
        {
            if (certificate is null)
                return null;

            // The same certificate is published once, under the name it was first added with.
            var thumbprint = certificate.GetCertHashString(HashAlgorithmName.SHA256);
            var known = certificates.FirstOrDefault(c => PdxPartnerPlanner.Thumbprint(c.Certificate) == thumbprint);
            if (known is not null)
                return known.Name;

            certificates.Add(new PdxCertificate
            {
                Name = name,
                SubjectName = certificate.Subject,
                Certificate = certificate.RawData,
                CaCertificates = Chain(certificate, station.TrustAnchors),
            });
            return name;
        }

        var fileRef = Reference(station.FileCertificate, FileName);
        PdxSupport Needs(SecurityUsage usage, string feature)
        {
            if (fileRef is not null || usage == SecurityUsage.Forbidden)
                return new PdxSupport(usage, fileRef is null ? [] : [fileRef]);

            warnings.Add($"{feature} is published as forbidden: no file security certificate is configured in the settings.");
            return new PdxSupport(SecurityUsage.Forbidden);
        }

        var inbound = new List<PdxInboundConnection>();
        if (station.Listener is { } listener)
        {
            if (string.IsNullOrWhiteSpace(profile.PublicHost))
                warnings.Add("No public host is set in the station profile: partners do not learn where to call us.");
            else
                inbound.Add(new PdxInboundConnection
                {
                    Type = listener.UseTls ? PdxConnectionType.Tls : PdxConnectionType.Tcp,
                    TlsVersions = listener.UseTls ? TlsVersions(listener.Tls) : [],
                    ServerHost = profile.PublicHost.Trim(),
                    ServerPort = profile.PublicPort ?? listener.Port,
                    CertificateRef = listener.UseTls ? Reference(station.TlsCertificate, TlsName) : null,
                    TlsClientAuth = listener.UseTls
                        ? listener.RequireClientCertificate ? SecurityUsage.Required : SecurityUsage.Optional
                        : null,
                });
        }
        else
        {
            warnings.Add("No listener is selected in the station profile: partners do not learn where to call us.");
        }

        var clientRef = Reference(station.ClientCertificate, ClientName);
        var clientAuth = clientRef is null
            ? new PdxSupport(SecurityUsage.Forbidden)
            : new PdxSupport(profile.TlsClientAuthentication, profile.TlsClientAuthentication == SecurityUsage.Forbidden ? [] : [clientRef]);

        // The schema of the datasheet knows the cipher suites up to 07 only.
        var accepted = CipherSuite.Supported.Where(s => s.InCommunicationSetup).Select(s => s.Code).ToList();
        var alternates = (profile.AlternateCipherSuites.Count == 0 ? accepted : profile.AlternateCipherSuites.Where(accepted.Contains))
            .Where(c => c != profile.PrimaryCipherSuite)
            .ToList();

        var document = new PdxDocument
        {
            Version = "1.2",
            DocId = Guid.NewGuid(),
            DocDate = station.Now,
            ValidFrom = station.ValidFrom ?? station.Now,
            Station = new PdxStationInformation
            {
                Duns = profile.Duns,
                Name = profile.CompanyName is { Length: > 0 } company ? company : identity.Name,
                Company = new PdxCompanyInfo
                {
                    Name = profile.CompanyName is { Length: > 0 } name ? name : identity.Name,
                    AddressLines = profile.AddressLines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList(),
                    City = profile.City,
                    ZipCode = profile.ZipCode,
                    Country = string.IsNullOrWhiteSpace(profile.Country) ? null : profile.Country.Trim().ToUpperInvariant(),
                },
                IssuerIdCode = identity.SSID.Trim(),
                InfoDocumentUrl = profile.InfoDocumentUrl,
                Contacts = profile.Contacts.Select(c => new PdxContact
                {
                    Name = c.Name,
                    Description = c.Description,
                    Phones = c.Phones,
                    Emails = c.Emails,
                    Url = c.Url,
                }).ToList(),
            },
            InboundConnections = inbound,
            OutboundConnections =
            [
                new PdxOutboundConnection
                {
                    Type = PdxConnectionType.Tls,
                    TlsVersions = TlsVersions(SslProtocols.Tls12 | SslProtocols.Tls13),
                    OutboundIps = profile.OutboundIps.Where(ip => !string.IsNullOrWhiteSpace(ip)).ToList(),
                    TlsClientAuth = clientAuth,
                },
            ],
            CipherSetting = new PdxCipherSetting(profile.PrimaryCipherSuite, alternates),
            Session = new PdxSessionSettings
            {
                Ssid = identity.SSID.Trim(),
                // Partners on the older ODETTE-FTP revisions are supported as well.
                MinimumProtocolReleaseLevel = ProtocolLevels.Oftp12,
                MaximumProtocolReleaseLevel = ProtocolLevels.Oftp2,
                Password = identity.Password,
                SecureAuthentication = Needs(profile.SecureAuthentication, "Secure authentication"),
            },
            OutboundFileSettings = new PdxFileSettings
            {
                FileSignature = Needs(profile.OutboundFileSignature, "Signing of files we send"),
                FileEncryption = new PdxSupport(profile.OutboundFileEncryption),
                FileCompression = new PdxSupport(profile.OutboundFileCompression),
                EerpSignature = new PdxSupport(profile.OutboundEerpSignature),
            },
            InboundFileSettings = new PdxFileSettings
            {
                FileSignature = new PdxSupport(profile.InboundFileSignature),
                FileEncryption = Needs(profile.InboundFileEncryption, "Encryption of files we receive"),
                FileCompression = new PdxSupport(profile.InboundFileCompression),
                EerpSignature = Needs(profile.InboundEerpSignature, "Signing of the EERPs we send"),
            },
            SubStations = station.SubStations.Select(s => new PdxSubStation { Name = s.Name, Sfid = s.SFID.Trim() }).ToList(),
            Certificates = certificates,
        };

        return (document, warnings);
    }

    private static List<string> TlsVersions(SslProtocols protocols)
    {
        if (protocols == SslProtocols.None)
            protocols = SslProtocols.Tls12 | SslProtocols.Tls13;

        var versions = new List<string>();
        if (protocols.HasFlag(SslProtocols.Tls12))
            versions.Add("TLSv1.2");
        if (protocols.HasFlag(SslProtocols.Tls13))
            versions.Add("TLSv1.3");
        return versions;
    }

    /// <summary>The certificates above ours up to the trust anchor, so that partners can check the whole chain.</summary>
    private static List<byte[]> Chain(X509Certificate2 certificate, X509Certificate2Collection anchors)
    {
        foreach (var custom in new[] { true, false })
        {
            if (custom && anchors.Count == 0)
                continue;

            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            if (custom)
            {
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.AddRange(anchors);
            }

            if (chain.Build(certificate))
                return chain.ChainElements.Skip(1).Select(e => e.Certificate.RawData).ToList();
        }

        return [];
    }
}
