using System.ComponentModel.DataAnnotations;

namespace Oftp4Net.Domain;

public class SendQueueItem: BaseQueueItem
{
    public int PartnerId { get; set; }
    public Partner Partner { get; set; } = null!;
    public int IdentityId { get; set; }
    public Identity Identity { get; set; } = null!;

    /// <summary>
    /// SFID of a sub-station of the partner the file is addressed to (SFIDDEST); empty for the partner itself.
    /// </summary>
    [StringLength(25)]
    public string? DestinationSfid { get; set; }
    
    [Required]
    [StringLength(26)]
    public string VirtualFileName { get; set; } = string.Empty;
    
    [Required]
    [StringLength(2000)]
    public string FilePath { get; set; } = string.Empty;

    [StringLength(999)]
    public string? Description { get; set; }

    public SendStatus Status { get; set; }

    /// <summary>Virtual file date stamp (CCYYMMDD) of the last transfer, used to match the End to End Response.</summary>
    [StringLength(8)]
    public string? FileDate { get; set; }

    /// <summary>Virtual file time stamp (HHMMSScccc) of the last transfer, used to match the End to End Response.</summary>
    [StringLength(10)]
    public string? FileTime { get; set; }

    /// <summary>Identification of the file in the calling system (REST API), for correlation.</summary>
    [StringLength(100)]
    public string? Reference { get; set; }

    /// <summary>URL notified about the progress of this file (REST API).</summary>
    [StringLength(500)]
    public string? WebhookUrl { get; set; }

    /// <summary>Secret used to sign the webhook request (header X-Oftp4Net-Signature).</summary>
    [StringLength(200)]
    public string? WebhookSecret { get; set; }

    public DateTime? SentDate { get; set; }
    public DateTime? DeliveredDate { get; set; }

    /// <summary>
    /// Complete 1K blocks the partner is believed to have from an interrupted transfer. The next attempt offers
    /// this position in SFID and the partner lowers it to what it really has.
    /// </summary>
    public long RestartPosition { get; set; }

    /// <summary>Hash of the transferred content (EERPHSH), verified against a signed End to End Response.</summary>
    public byte[]? ContentHash { get; set; }

    /// <summary>Cipher suite the file was secured with (SFIDCIPH), empty when it was sent unsecured.</summary>
    [StringLength(2)]
    public string? CipherSuite { get; set; }

    /// <summary>A signed End to End Response was requested for the last transfer (SFIDSIGN).</summary>
    public bool SignedResponseRequested { get; set; }
}
