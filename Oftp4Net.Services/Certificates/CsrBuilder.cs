using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Oftp4Net.Services.Certificates;

/// <summary>What the certificate is to say about its owner.</summary>
public sealed class CsrOptions
{
    /// <summary>Common name: the host name partners call (TLS), e.g. oftp.example.com.</summary>
    public string CommonName { get; set; } = string.Empty;

    public string? Organization { get; set; }
    public string? OrganizationalUnit { get; set; }
    public string? Locality { get; set; }
    public string? State { get; set; }

    /// <summary>ISO 3166 two letter country code.</summary>
    public string? Country { get; set; }

    /// <summary>The Odette ID (SSID) of the station, put into the serial number attribute (OFTP2 Certificate Policy).</summary>
    public string? OdetteId { get; set; }

    public string? Email { get; set; }

    /// <summary>Further host names or addresses the certificate is valid for; the common name is always included.</summary>
    public List<string> AlternativeNames { get; set; } = [];

    public int KeySize { get; set; } = 3072;
}

/// <summary>
/// Creates a certificate signing request with the profile of the Odette OFTP2 Certificate Policy: RSA with SHA-256,
/// key usage digital signature and key encipherment (TLS, file signing and encryption), extended key usage TLS server
/// and client authentication (not critical), the host name in the common name and as a subject alternative name.
/// </summary>
public static class CsrBuilder
{
    public static readonly int[] KeySizes = [2048, 3072, 4096];

    /// <summary>The request in PEM and its new private key (the caller disposes it).</summary>
    public static (string CsrPem, RSA Key) Create(CsrOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.CommonName))
            throw new ArgumentException("The common name (host name) is required.", nameof(options));
        if (!KeySizes.Contains(options.KeySize))
            throw new ArgumentException($"The key size must be one of {string.Join(", ", KeySizes)} bits.", nameof(options));
        if (!string.IsNullOrWhiteSpace(options.Country) && options.Country.Trim().Length != 2)
            throw new ArgumentException("The country must be a two letter ISO code, e.g. CZ.", nameof(options));

        var key = RSA.Create(options.KeySize);
        try
        {
            var request = new CertificateRequest(SubjectName(options), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
            // Not critical, as the OFTP2 Certificate Policy asks, so that no implementation refuses the certificate.
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                [new Oid("1.3.6.1.5.5.7.3.1", "TLS server"), new Oid("1.3.6.1.5.5.7.3.2", "TLS client")], critical: false));
            request.CertificateExtensions.Add(AlternativeNames(options));

            return (request.CreateSigningRequestPem(), key);
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    /// <summary>The subject: C, ST, L, O, OU, CN, serial number (Odette ID) and e-mail, those that are given.</summary>
    public static X500DistinguishedName SubjectName(CsrOptions options)
    {
        var builder = new X500DistinguishedNameBuilder();
        if (!string.IsNullOrWhiteSpace(options.Country))
            builder.AddCountryOrRegion(options.Country.Trim().ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(options.State))
            builder.AddStateOrProvinceName(options.State.Trim());
        if (!string.IsNullOrWhiteSpace(options.Locality))
            builder.AddLocalityName(options.Locality.Trim());
        if (!string.IsNullOrWhiteSpace(options.Organization))
            builder.AddOrganizationName(options.Organization.Trim());
        if (!string.IsNullOrWhiteSpace(options.OrganizationalUnit))
            builder.AddOrganizationalUnitName(options.OrganizationalUnit.Trim());
        builder.AddCommonName(options.CommonName.Trim());
        if (!string.IsNullOrWhiteSpace(options.OdetteId))
            builder.Add("2.5.4.5", options.OdetteId.Trim(), System.Formats.Asn1.UniversalTagNumber.PrintableString);
        if (!string.IsNullOrWhiteSpace(options.Email))
            builder.AddEmailAddress(options.Email.Trim());
        return builder.Build();
    }

    private static X509Extension AlternativeNames(CsrOptions options)
    {
        var builder = new SubjectAlternativeNameBuilder();
        foreach (var name in options.AlternativeNames.Prepend(options.CommonName).Select(n => n.Trim())
                     .Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (IPAddress.TryParse(name, out var address))
                builder.AddIpAddress(address);
            else
                builder.AddDnsName(name);
        }

        if (!string.IsNullOrWhiteSpace(options.Email))
            builder.AddEmailAddress(options.Email.Trim());
        return builder.Build();
    }

    /// <summary>
    /// Reads the certificate returned by the certification authority: PEM (possibly with the chain), DER or PKCS#7.
    /// Returns the certificate that belongs to <paramref name="key"/> and the other certificates of the file.
    /// </summary>
    public static (X509Certificate2 Certificate, X509Certificate2Collection Chain) ReadSigned(byte[] content, RSA key)
    {
        var all = new X509Certificate2Collection();
        var text = Encoding.ASCII.GetString(content);
        try
        {
            if (text.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal))
                all.ImportFromPem(text);
            else
                all.Add(X509CertificateLoader.LoadCertificate(content));
        }
        catch (CryptographicException)
        {
            // Not a single certificate: a PKCS#7 bundle (.p7b / .p7c).
            all.Clear();
#pragma warning disable SYSLIB0057 // PKCS#7 bundles have no replacement in X509CertificateLoader.
            all.Import(content);
#pragma warning restore SYSLIB0057
        }

        var publicKey = key.ExportSubjectPublicKeyInfo();
        var own = all.FirstOrDefault(c => c.PublicKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(publicKey))
                  ?? throw new CryptographicException("The file contains no certificate for the key of this request.");

        var chain = new X509Certificate2Collection(all.Where(c => !ReferenceEquals(c, own)).ToArray());
        return (own, chain);
    }
}
