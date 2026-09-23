using System.ComponentModel.DataAnnotations;

namespace Oftp4Net.Domain;

/// <summary>
/// A certificate signing request (CSR) created here for a certification authority, e.g. the Odette CA. The private
/// key stays on this server; when the CA returns the signed certificate, both are stored together as a certificate
/// with a private key and the key is removed from the request.
/// </summary>
public class CertificateSigningRequest
{
    public int Id { get; set; }
    public DateTime Created { get; set; } = DateTime.Now;

    [Required]
    [StringLength(100)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Distinguished name of the subject, e.g. "CN=oftp.example.com, O=Example Ltd., C=CZ".</summary>
    [Required]
    [StringLength(1000)]
    public string Subject { get; set; } = string.Empty;

    public int KeySize { get; set; }

    /// <summary>The request in PEM ("-----BEGIN CERTIFICATE REQUEST-----"), sent to the certification authority.</summary>
    [Required]
    public string Csr { get; set; } = string.Empty;

    /// <summary>The private key (PKCS#8, base64), encrypted in the database; removed when the request is completed.</summary>
    public string? PrivateKey { get; set; }

    /// <summary>The certificate created from the signed request.</summary>
    public int? CertificateId { get; set; }
    public Certificate? Certificate { get; set; }

    public DateTime? CompletedDate { get; set; }
}
