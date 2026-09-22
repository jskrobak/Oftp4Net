using System.ComponentModel.DataAnnotations;

namespace Oftp4Net.Domain;

public class SendQueueItem: BaseQueueItem
{
    public int PartnerId { get; set; }
    public Partner Partner { get; set; } = null!;
    public int IdentityId { get; set; }
    public Identity Identity { get; set; } = null!;
    
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

    public DateTime? SentDate { get; set; }
    public DateTime? DeliveredDate { get; set; }
}
