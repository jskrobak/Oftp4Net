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
}
