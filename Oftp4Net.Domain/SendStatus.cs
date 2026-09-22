namespace Oftp4Net.Domain;

public enum SendStatus
{
    /// <summary>Waiting to be sent (also after a failed attempt that will be retried).</summary>
    NEW = 0,

    /// <summary>Sending failed, will be retried at <see cref="BaseQueueItem.NextRetry"/>.</summary>
    ERROR = 42,

    /// <summary>Permanently failed: refused by the partner without retry or retry limit reached.</summary>
    FAILED = 43,

    /// <summary>The partner confirmed the file with a Negative End Response (NERP).</summary>
    NOT_DELIVERED = 44,

    /// <summary>Transferred, the partner confirmed receipt (EFPA). Waiting for End to End Response.</summary>
    SENT = 999,

    /// <summary>The partner confirmed delivery with an End to End Response (EERP).</summary>
    DELIVERED = 1000,
}
