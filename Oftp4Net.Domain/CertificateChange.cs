using System.ComponentModel.DataAnnotations;

namespace Oftp4Net.Domain;

/// <summary>What a certificate of a partner is used for.</summary>
public enum PartnerCertificateUsage
{
    /// <summary>
    /// File security (<see cref="Partner.SecurityCertificate"/>): files are encrypted for it and the partner's signatures
    /// are verified against it. The one before is kept as <see cref="Partner.PreviousSecurityCertificate"/> for the roll-over.
    /// </summary>
    Security,

    /// <summary>Trusting the partner's TLS server certificate (<see cref="Partner.TrustedCertificate"/>).</summary>
    Tls,

    /// <summary>Both at once, the usual case of an Odette certificate used for files and TLS.</summary>
    SecurityAndTls,
}

/// <summary>Whose certificate a change replaces.</summary>
public enum CertificateChangeTarget
{
    /// <summary>The certificate of a partner (<see cref="CertificateChange.Partner"/>).</summary>
    Partner,

    /// <summary>
    /// Our own certificate with its private key: <see cref="CertificateChange.ReplacedCertificate"/> is replaced
    /// wherever we use it for the purpose, in the settings, at our identities and at the listeners.
    /// </summary>
    Own,
}

public enum CertificateChangeStatus
{
    /// <summary>Waits for its time.</summary>
    Scheduled,

    /// <summary>The certificate is in use since <see cref="CertificateChange.AppliedAt"/>.</summary>
    Applied,

    /// <summary>Cancelled by an administrator before its time.</summary>
    Cancelled,

    /// <summary>Could not be applied (e.g. the partner or the certificate was deleted), see <see cref="CertificateChange.LastError"/>.</summary>
    Failed,
}

/// <summary>
/// A new certificate of a partner uploaded in advance: it replaces the current one at <see cref="ActivateAt"/>, the
/// moment the partner starts using it. Or a new certificate of ours (<see cref="CertificateChangeTarget.Own"/>) that
/// replaces the one we use at <see cref="ActivateAt"/>, announced to the partners in advance.
/// </summary>
public class CertificateChange
{
    public int Id { get; set; }

    public CertificateChangeTarget Target { get; set; } = CertificateChangeTarget.Partner;

    public int? PartnerId { get; set; }
    public Partner? Partner { get; set; }

    /// <summary>Name of the partner, kept for the history when the partner is deleted.</summary>
    [StringLength(50)]
    public string PartnerName { get; set; } = string.Empty;

    public int? CertificateId { get; set; }
    public Certificate? Certificate { get; set; }

    /// <summary>
    /// What the certificate is used for. For our own certificate file security means signing and decrypting files
    /// and responses and answering authentication challenges, TLS the client certificate and the listeners.
    /// </summary>
    public PartnerCertificateUsage Usage { get; set; } = PartnerCertificateUsage.Security;

    /// <summary>Our certificate the change replaces (<see cref="CertificateChangeTarget.Own"/> only).</summary>
    public int? ReplacedCertificateId { get; set; }
    public Certificate? ReplacedCertificate { get; set; }

    /// <summary>
    /// Until when the replaced certificate of ours still decrypts files and authentication challenges of partners
    /// that have not taken over the new one yet (<see cref="CertificateChangeTarget.Own"/> only); <c>null</c> until
    /// it expires.
    /// </summary>
    public DateTime? KeepPreviousUntil { get; set; }

    /// <summary>Local time the certificate is used from.</summary>
    public DateTime ActivateAt { get; set; }

    public CertificateChangeStatus Status { get; set; } = CertificateChangeStatus.Scheduled;

    public DateTime Created { get; set; } = DateTime.Now;

    [StringLength(50)]
    public string? CreatedBy { get; set; }

    public DateTime? AppliedAt { get; set; }

    [StringLength(500)]
    public string? Note { get; set; }

    [StringLength(1000)]
    public string? LastError { get; set; }
}
