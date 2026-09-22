using System.Security.Cryptography.X509Certificates;
using Oftp4Net.Domain;

namespace Oftp4Net.Services.Oftp;

public static class CertificateLoader
{
    /// <summary>Creates an <see cref="X509Certificate2"/> from a stored certificate (PKCS#12 with key, or DER/PEM).</summary>
    public static X509Certificate2 Load(Certificate certificate)
    {
        if (string.IsNullOrEmpty(certificate.Base64Data))
            throw new InvalidOperationException($"Certificate '{certificate.Name}' has no data.");

        var data = Convert.FromBase64String(certificate.Base64Data);

        return certificate.HasPrivateKey
            ? X509CertificateLoader.LoadPkcs12(data, certificate.Password)
            : X509CertificateLoader.LoadCertificate(data);
    }

    /// <summary>
    /// Creates the database record of a certificate file: PKCS#12 (.pfx/.p12, with private key) or DER/PEM.
    /// </summary>
    public static Certificate CreateEntity(byte[] data, string fileName, string? password)
    {
        var isPkcs12 = Path.GetExtension(fileName).ToLowerInvariant() is ".pfx" or ".p12";
        using var cert = isPkcs12
            ? X509CertificateLoader.LoadPkcs12(data, password)
            : X509CertificateLoader.LoadCertificate(data);

        return new Certificate
        {
            Name = cert.Subject,
            HasPrivateKey = cert.HasPrivateKey,
            ValidFrom = cert.NotBefore,
            ValidTo = cert.NotAfter,
            Base64Data = Convert.ToBase64String(data),
            Password = cert.HasPrivateKey ? password ?? string.Empty : string.Empty,
        };
    }
}
