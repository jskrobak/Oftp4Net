using System.ComponentModel.DataAnnotations;

namespace Oftp4Net.Domain;

/// <summary>What a partner answers about taking over our new certificate.</summary>
public enum AnnouncementTakeover
{
    /// <summary>It will import the certificate by <see cref="CertificateAnnouncementLink.PlannedAt"/>.</summary>
    WillImport,

    /// <summary>The certificate is imported already.</summary>
    Imported,

    /// <summary>It needs help; the note says with what.</summary>
    NeedsHelp,
}

/// <summary>How a partner checks the TLS certificate of our server.</summary>
public enum AnnouncementTlsTrust
{
    Unknown,

    /// <summary>It trusts the certification authority that issued it: nothing to do for TLS.</summary>
    CertificationAuthority,

    /// <summary>It trusts our certificate itself (pinned): it has to import the new one for TLS too.</summary>
    Pinned,

    /// <summary>It does not check our certificate.</summary>
    NotChecked,
}

/// <summary>
/// The questionnaire of one e-mail of the announcement of a new certificate of ours: a link with a random token sent
/// to the recipients, the partners it concerns and the last answer. Answers come from the internet without sign in;
/// they are shown to the administrator and taken over only by hand.
/// </summary>
public class CertificateAnnouncementLink
{
    public int Id { get; set; }

    public int CertificateChangeId { get; set; }
    public CertificateChange? CertificateChange { get; set; }

    /// <summary>The recipients of the e-mail, as the announcement groups them.</summary>
    [StringLength(2000)]
    public string RecipientKey { get; set; } = string.Empty;

    /// <summary>The partners the e-mail names.</summary>
    public List<int> PartnerIds { get; set; } = [];

    /// <summary>Random part of the address of the questionnaire; whoever has it may answer.</summary>
    [StringLength(64)]
    public string Token { get; set; } = string.Empty;

    public DateTime Created { get; set; } = DateTime.Now;

    /// <summary>The questionnaire answers no more after this time.</summary>
    public DateTime ExpiresAt { get; set; }

    public DateTime? RespondedAt { get; set; }

    /// <summary>Address the answer came from.</summary>
    [StringLength(100)]
    public string? RemoteAddress { get; set; }

    public AnnouncementTakeover? Takeover { get; set; }
    public DateTime? PlannedAt { get; set; }
    public AnnouncementTlsTrust? TlsTrust { get; set; }

    /// <summary>Whether the partner lets only known addresses connect to it.</summary>
    public bool? FiltersByIp { get; set; }

    [StringLength(200)]
    public string? ContactName { get; set; }

    [StringLength(200)]
    public string? ContactEmail { get; set; }

    [StringLength(100)]
    public string? ContactPhone { get; set; }

    [StringLength(2000)]
    public string? Note { get; set; }

    /// <summary>When the contact of the answer was added to the partners.</summary>
    public DateTime? ContactTakenOverAt { get; set; }
}
