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

    [StringLength(2000)]
    public string? LastError { get; set; }
}
