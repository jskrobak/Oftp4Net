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
/// moment the partner starts using it.
/// </summary>
public class CertificateChange
{
    public int Id { get; set; }

    public int? PartnerId { get; set; }
    public Partner? Partner { get; set; }

    /// <summary>Name of the partner, kept for the history when the partner is deleted.</summary>
    [StringLength(50)]
    public string PartnerName { get; set; } = string.Empty;

    public int? CertificateId { get; set; }
    public Certificate? Certificate { get; set; }

    public PartnerCertificateUsage Usage { get; set; } = PartnerCertificateUsage.Security;

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
