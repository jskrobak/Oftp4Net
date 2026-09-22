using System.ComponentModel.DataAnnotations;

namespace Oftp4Net.Domain;

public enum TransferEventCategory
{
    /// <summary>Connections we open and files we send.</summary>
    Outgoing,

    /// <summary>Connections partners open to us and files we receive.</summary>
    Incoming,

    /// <summary>End to End Responses (EERP / NERP) in both directions.</summary>
    EndResponse,

    /// <summary>Runs of event hook scripts.</summary>
    Hook,
}

public enum TransferEventLevel
{
    Information,
    Warning,
    Error,
}

public enum TransferEventType
{
    SessionStarted,
    SessionEnded,
    SessionFailed,
    AuthenticationRejected,
    FileSent,
    FileSendFailed,
    FileReceived,
    FileRefused,
    FileReceiveFailed,
    EerpSent,
    EerpReceived,
    NerpReceived,
    UnknownEndResponse,
    HookFinished,
    HookFailed,
}

/// <summary>
/// Audit record of the transfer history shown in the Logs section. Records older than the archive period are marked
/// as archived; they are kept but hidden unless the complete archive is requested.
/// </summary>
/// <remarks>Partner and file data are copied, so the history survives deleting partners or queue items.</remarks>
public class TransferEvent
{
    public int Id { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public TransferEventCategory Category { get; set; }
    public TransferEventLevel Level { get; set; }
    public TransferEventType Type { get; set; }

    public int? PartnerId { get; set; }

    [StringLength(50)]
    public string? PartnerName { get; set; }

    [StringLength(100)]
    public string? RemoteEndPoint { get; set; }

    [StringLength(26)]
    public string? VirtualFileName { get; set; }

    [StringLength(8)]
    public string? FileDate { get; set; }

    [StringLength(10)]
    public string? FileTime { get; set; }

    public long? FileSize { get; set; }
    public int? SendQueueItemId { get; set; }
    public int? ReceivedFileId { get; set; }

    [StringLength(1000)]
    public string Message { get; set; } = string.Empty;

    /// <summary>Additional text: error details, hook output, …</summary>
    public string? Details { get; set; }

    public long? DurationMs { get; set; }

    public bool IsArchived { get; set; }
}
