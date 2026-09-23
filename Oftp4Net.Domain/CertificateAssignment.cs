namespace Oftp4Net.Domain;

/// <summary>
/// What a certificate is used for in the relationship with one station (Odette OP08 2.5: certificates are bound to
/// a combination of a local and a remote entity and to a purpose). A station that uses one certificate for
/// everything needs no assignment at all.
/// </summary>
public enum CertificateUsage
{
    /// <summary>Signing the content of files and verifying the signature of the partner's files (SFIDSEC).</summary>
    FileSignature,

    /// <summary>Encrypting files for the partner and decrypting the files it sends us (SFIDSEC).</summary>
    FileEncryption,

    /// <summary>Signing End to End Responses and verifying the ones the partner signs (SFIDSIGN).</summary>
    EndResponse,

    /// <summary>Secure authentication of the session (SSIDAUTH with SECD, AUCH and AURP).</summary>
    Authentication,
}

/// <summary>
/// One certificate of a station for one purpose. On a partner it is the certificate of the partner (public part
/// only), on an identity our own certificate with its private key.
/// </summary>
public class CertificateAssignment
{
    /// <summary>
    /// Station the assignment belongs to (SFIDORIG of files it sends, SFIDDEST of files we send to it); empty for
    /// the party itself, which is also what an identity uses.
    /// </summary>
    public string? Sfid { get; set; }

    public CertificateUsage Usage { get; set; }

    public int CertificateId { get; set; }

    /// <summary>
    /// The certificate this one replaced. It stays valid for the roll-over period, so that files and responses
    /// that were already on their way are still accepted (Odette OP08 2.5 F).
    /// </summary>
    public int? PreviousCertificateId { get; set; }
}
