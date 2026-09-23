using System.ComponentModel.DataAnnotations;

namespace Oftp4Net.Domain;

/// <summary>A virtual file received from a partner.</summary>
public class ReceivedFile
{
    public int Id { get; set; }
    public DateTime Created { get; set; } = DateTime.Now;

    public int? PartnerId { get; set; }
    public Partner? Partner { get; set; }

    [Required]
    [StringLength(26)]
    public string VirtualFileName { get; set; } = string.Empty;

    /// <summary>Virtual file date stamp CCYYMMDD as sent in SFID, echoed back in EERP.</summary>
    [StringLength(8)]
    public string FileDate { get; set; } = string.Empty;

    /// <summary>Virtual file time stamp HHMMSScccc as sent in SFID, echoed back in EERP.</summary>
    [StringLength(10)]
    public string FileTime { get; set; } = string.Empty;

    [StringLength(8)]
    public string UserData { get; set; } = string.Empty;

    /// <summary>SFID of the partner that created the file.</summary>
    [StringLength(25)]
    public string Originator { get; set; } = string.Empty;

    /// <summary>SFID of our identity the file was addressed to.</summary>
    [StringLength(25)]
    public string Destination { get; set; } = string.Empty;

    [StringLength(999)]
    public string? Description { get; set; }

    [StringLength(2000)]
    public string FilePath { get; set; } = string.Empty;

    public long Size { get; set; }

    public ReceiveStatus Status { get; set; }

    public DateTime? ConfirmedDate { get; set; }

    /// <summary>When the file was fetched through the REST API.</summary>
    public DateTime? FetchedDate { get; set; }

    [StringLength(2000)]
    public string? LastError { get; set; }

    /// <summary>Security of the transferred content as announced in SFIDSEC.</summary>
    [StringLength(2)]
    public string? SecurityLevel { get; set; }

    /// <summary>Cipher suite of the transferred content (SFIDCIPH).</summary>
    [StringLength(2)]
    public string? CipherSuite { get; set; }

    /// <summary>The content was compressed in transfer (SFIDCOMP).</summary>
    public bool Compressed { get; set; }

    /// <summary>The partner asked for a signed End to End Response of this file (SFIDSIGN).</summary>
    public bool SignedResponseRequested { get; set; }

    /// <summary>
    /// Position (in 1K blocks) the transfer was continued from after an interruption, zero when the file
    /// arrived in one go.
    /// </summary>
    public long RestartedFrom { get; set; }

    /// <summary>
    /// Answer reason code of the Negative End Response sent for this file (NERPREAS); set together with the
    /// state <see cref="ReceiveStatus.NOT_DELIVERED"/>, the text is in <see cref="LastError"/>.
    /// </summary>
    [StringLength(2)]
    public string? NotDeliveredReasonCode { get; set; }

    /// <summary>Hash of the transferred content, sent back in EERPHSH.</summary>
    public byte[]? ContentHash { get; set; }
}
