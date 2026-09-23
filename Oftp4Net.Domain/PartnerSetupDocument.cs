using System.ComponentModel.DataAnnotations;

namespace Oftp4Net.Domain;

/// <summary>Where an OFTP2 Communication Setup came from.</summary>
public enum PartnerSetupSource
{
    /// <summary>Uploaded in the user interface (e.g. received by e-mail).</summary>
    Upload = 0,

    /// <summary>Received from the partner over OFTP (virtual file OFTP_COMMUNICATION_SETUP).</summary>
    Oftp = 1,
}

/// <summary>State of the signature of a datasheet received over OFTP.</summary>
public enum PartnerSetupSignature
{
    /// <summary>The file was not signed.</summary>
    None = 0,

    /// <summary>Signed with the certificate configured for the partner.</summary>
    Valid = 1,

    /// <summary>Signed, but the signature could not be verified (e.g. an unknown certificate).</summary>
    Invalid = 2,
}

public enum PartnerSetupStatus
{
    /// <summary>Waits for the administrator; the End to End Response is sent after the decision.</summary>
    PendingApproval = 0,

    /// <summary>Accepted; it is applied when the End to End Response has been sent to the partner.</summary>
    AwaitingEndResponse = 1,

    /// <summary>Accepted and confirmed, it is applied at <see cref="PartnerSetupDocument.ValidFrom"/>.</summary>
    Scheduled = 2,

    Applied = 3,

    /// <summary>Not accepted: incompatible, invalid or refused by the administrator (a NERP is sent).</summary>
    Rejected = 4,

    /// <summary>Accepted, but it could not be applied later (e.g. the station profile changed meanwhile).</summary>
    Failed = 5,
}

/// <summary>
/// An OFTP2 Communication Setup (PDX) of a partner: the history of imported datasheets and the queue of the ones
/// received over OFTP that are waiting for approval or for their time.
/// </summary>
public class PartnerSetupDocument
{
    public int Id { get; set; }
    public DateTime Created { get; set; } = DateTime.Now;

    public int? PartnerId { get; set; }
    public Partner? Partner { get; set; }

    /// <summary>SSID of the station described by the datasheet, kept when the partner is deleted.</summary>
    [StringLength(25)]
    public string Ssid { get; set; } = string.Empty;

    [StringLength(200)]
    public string StationName { get; set; } = string.Empty;

    public PartnerSetupSource Source { get; set; }

    /// <summary>The virtual file the datasheet was received in (source <see cref="PartnerSetupSource.Oftp"/>).</summary>
    public int? ReceivedFileId { get; set; }
    public ReceivedFile? ReceivedFile { get; set; }

    public PartnerSetupSignature Signature { get; set; }

    public Guid DocId { get; set; }
    public DateTime DocDate { get; set; }
    public DateTime ValidFrom { get; set; }

    /// <summary>The datasheet as received (XML).</summary>
    [Required]
    public string Content { get; set; } = string.Empty;

    public PartnerSetupStatus Status { get; set; }

    /// <summary>Errors and warnings of the last evaluation, one per line.</summary>
    [StringLength(4000)]
    public string? Messages { get; set; }

    /// <summary>The changes of the partner, one per line, as shown before it was applied.</summary>
    [StringLength(4000)]
    public string? Changes { get; set; }

    public DateTime? DecidedDate { get; set; }

    [StringLength(100)]
    public string? DecidedBy { get; set; }

    public DateTime? AppliedDate { get; set; }
}
