using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Xml;

namespace Oftp4Net.Services.Tsl;

/// <summary>The Trust Service Status List cannot be used: it is malformed or its signature is not valid.</summary>
public class TslException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>A certification authority listed in the Trust Service Status List.</summary>
public sealed record TrustServiceProvider(string Provider, string Service, string Status, IReadOnlyList<X509Certificate2> Certificates)
{
    /// <summary>Only services "in accord" with the OFTP2 Certificate Policy are trusted.</summary>
    public bool Trusted => Status == TrustServiceList.InAccord;
}

/// <summary>
/// The Odette Trust Service Status List (TSL, ETSI TS 102 231 v2): the certification authorities whose certificates
/// are accepted for OFTP2. The list is signed (XMLDSig, enveloped); <see cref="Parse"/> verifies the signature.
/// </summary>
public sealed class TrustServiceList
{
    public const string Namespace = "http://uri.etsi.org/02231/v2#";
    public const string InAccord = "http://uri.etsi.org/TrstSvc/Svcstatus/inaccord";

    public required string SchemeName { get; init; }
    public required long SequenceNumber { get; init; }
    public required DateTimeOffset IssueDate { get; init; }
    public DateTimeOffset? NextUpdate { get; init; }

    /// <summary>The certificate the list is signed with.</summary>
    public required X509Certificate2 Signer { get; init; }

    public required IReadOnlyList<TrustServiceProvider> Providers { get; init; }

    /// <summary>Certificates of the trusted services (roots and intermediates), used as trust anchors.</summary>
    public X509Certificate2Collection TrustAnchors =>
        new(Providers.Where(p => p.Trusted).SelectMany(p => p.Certificates).DistinctBy(c => c.Thumbprint).ToArray());

    /// <summary>
    /// Certificates that are in the list to verify the certification authority above them and not to issue
    /// certificates of their own: Odette lists the root of every OFTP2 authority for that reason. Within one
    /// service entry these are the certificates that issued another certificate of the same entry; a certificate
    /// that is an authority in its own entry somewhere else is not one of them.
    /// </summary>
    public X509Certificate2Collection VerificationRoots
    {
        get
        {
            var trusted = Providers.Where(p => p.Trusted).ToList();
            var authorities = trusted
                .SelectMany(p => p.Certificates.Where(c => !IssuedAnother(c, p.Certificates)))
                .Select(c => c.Thumbprint)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return new X509Certificate2Collection(trusted
                .SelectMany(p => p.Certificates.Where(c => IssuedAnother(c, p.Certificates)))
                .Where(c => !authorities.Contains(c.Thumbprint))
                .DistinctBy(c => c.Thumbprint)
                .ToArray());
        }
    }

    private static bool IssuedAnother(X509Certificate2 certificate, IReadOnlyList<X509Certificate2> others) =>
        others.Any(other => !ReferenceEquals(other, certificate) &&
                            other.IssuerName.RawData.AsSpan().SequenceEqual(certificate.SubjectName.RawData));

    /// <summary>SHA-256 thumbprint of <see cref="Signer"/>.</summary>
    public string SignerThumbprint => Signer.GetCertHashString(HashAlgorithmName.SHA256);

    /// <summary>Reads the list and verifies its signature against the certificate embedded in it.</summary>
    public static TrustServiceList Parse(byte[] content)
    {
        var document = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        try
        {
            using var stream = new MemoryStream(content);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            document.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new TslException($"The trust list is not valid XML: {ex.Message}", ex);
        }

        var root = document.DocumentElement;
        if (root is null || root.LocalName != "TrustServiceStatusList" || root.NamespaceURI != Namespace)
            throw new TslException("The document is not a Trust Service Status List (ETSI TS 102 231 v2).");

        var signer = VerifySignature(document, root);

        var ns = new XmlNamespaceManager(document.NameTable);
        ns.AddNamespace("t", Namespace);

        var scheme = root.SelectSingleNode("t:SchemeInformation", ns) ?? throw new TslException("SchemeInformation is missing.");

        return new TrustServiceList
        {
            SchemeName = scheme.SelectSingleNode("t:SchemeName/t:Name", ns)?.InnerText.Trim() ?? "",
            SequenceNumber = long.TryParse(scheme.SelectSingleNode("t:TSLSequenceNumber", ns)?.InnerText, CultureInfo.InvariantCulture, out var sequence)
                ? sequence
                : throw new TslException("TSLSequenceNumber is missing."),
            IssueDate = Date(scheme.SelectSingleNode("t:ListIssueDateTime", ns)) ?? throw new TslException("ListIssueDateTime is missing."),
            NextUpdate = Date(scheme.SelectSingleNode("t:NextUpdate/t:dateTime", ns)),
            Signer = signer,
            Providers = ReadProviders(root, ns),
        };
    }

    /// <summary>
    /// The signature must be the enveloped signature of the whole document (one reference, URI ""), so that no part of
    /// the list can be replaced without breaking it.
    /// </summary>
    private static X509Certificate2 VerifySignature(XmlDocument document, XmlElement root)
    {
        var signatures = root.GetElementsByTagName("Signature", SignedXml.XmlDsigNamespaceUrl);
        if (signatures.Count != 1 || signatures[0]!.ParentNode != root)
            throw new TslException("The trust list is not signed.");

        var signedXml = new SignedXml(document);
        try
        {
            signedXml.LoadXml((XmlElement)signatures[0]!);
        }
        catch (CryptographicException ex)
        {
            throw new TslException($"The signature of the trust list cannot be read: {ex.Message}", ex);
        }

        if (signedXml.SignedInfo?.References is not { Count: 1 } references || ((Reference)references[0]!).Uri != "")
            throw new TslException("The signature of the trust list does not cover the whole list.");

        var certificate = signedXml.KeyInfo.OfType<KeyInfoX509Data>()
            .SelectMany(d => d.Certificates?.OfType<X509Certificate>() ?? [])
            .Select(c => X509CertificateLoader.LoadCertificate(c.GetRawCertData()))
            .FirstOrDefault()
            ?? throw new TslException("The signature of the trust list contains no certificate.");

        bool valid;
        try
        {
            valid = signedXml.CheckSignature(certificate, verifySignatureOnly: true);
        }
        catch (CryptographicException ex)
        {
            throw new TslException($"The signature of the trust list cannot be verified: {ex.Message}", ex);
        }

        if (!valid)
            throw new TslException("The signature of the trust list is not valid, the list was changed after it was signed.");

        return certificate;
    }

    private static List<TrustServiceProvider> ReadProviders(XmlElement root, XmlNamespaceManager ns)
    {
        var providers = new List<TrustServiceProvider>();
        foreach (XmlNode provider in root.SelectNodes("t:TrustServiceProviderList/t:TrustServiceProvider", ns)!)
        {
            var providerName = provider.SelectSingleNode("t:TSPInformation/t:TSPName/t:Name", ns)?.InnerText.Trim() ?? "";
            foreach (XmlNode service in provider.SelectNodes("t:TSPServices/t:TSPService/t:ServiceInformation", ns)!)
            {
                var certificates = new List<X509Certificate2>();
                foreach (XmlNode data in service.SelectNodes("t:ServiceDigitalIdentity/t:DigitalId/t:X509Certificate", ns)!)
                {
                    try
                    {
                        certificates.Add(X509CertificateLoader.LoadCertificate(Convert.FromBase64String(data.InnerText.Trim())));
                    }
                    catch (Exception ex) when (ex is FormatException or CryptographicException)
                    {
                        // A broken entry cannot be trusted; the rest of the list is still usable.
                    }
                }

                providers.Add(new TrustServiceProvider(
                    providerName,
                    service.SelectSingleNode("t:ServiceName/t:Name", ns)?.InnerText.Trim() ?? "",
                    service.SelectSingleNode("t:ServiceStatus", ns)?.InnerText.Trim() ?? "",
                    certificates));
            }
        }

        return providers;
    }

    private static DateTimeOffset? Date(XmlNode? node) =>
        node is null ? null : XmlConvert.ToDateTimeOffset(node.InnerText.Trim());
}
