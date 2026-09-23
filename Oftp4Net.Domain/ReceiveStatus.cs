namespace Oftp4Net.Domain;

public enum ReceiveStatus
{
    /// <summary>Transfer in progress.</summary>
    RECEIVING = 0,

    /// <summary>Stored, End to End Response not yet sent to the partner.</summary>
    RECEIVED = 1,

    /// <summary>End to End Response (EERP) delivered to the partner.</summary>
    CONFIRMED = 2,

    /// <summary>Transfer did not complete.</summary>
    FAILED = 3,

    /// <summary>
    /// Transfer did not complete, but what was received is kept so that the partner can continue it
    /// (restart, SSIDREST).
    /// </summary>
    INTERRUPTED = 4,

    /// <summary>
    /// The file arrived, but could not be delivered to its final destination. A Negative End Response (NERP)
    /// is sent to the partner instead of the EERP.
    /// </summary>
    NOT_DELIVERED = 5,

    /// <summary>Stored, the End to End Response waits for a decision of the administrator (e.g. a datasheet).</summary>
    HELD = 6,
}
