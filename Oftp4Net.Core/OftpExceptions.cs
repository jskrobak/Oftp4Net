namespace Oftp4Net.Core;

public class OftpException(string message, Exception? innerException = null) : Exception(message, innerException);

public class OftpEncodingException(string message) : OftpException(message);

public class OftpDecodingException(string message) : OftpException(message);

/// <summary>
/// The peer violated the protocol or sent data this implementation cannot handle.
/// <see cref="ReasonCode"/> is the ESID reason sent (or to be sent) to the peer.
/// </summary>
public class OftpProtocolException(string reasonCode, string message) : OftpException(message)
{
    public string ReasonCode { get; } = reasonCode;
}

/// <summary>
/// The session was terminated by the peer with an ESID command other than normal termination.
/// </summary>
public class OftpSessionAbortedException(string reasonCode, string reasonText)
    : OftpException($"Session ended by peer with reason {reasonCode}: {reasonText}")
{
    public string ReasonCode { get; } = reasonCode;
    public string ReasonText { get; } = reasonText;
}

/// <summary>
/// The underlying network connection was closed unexpectedly.
/// </summary>
public class OftpConnectionClosedException(string message, Exception? innerException = null)
    : OftpException(message, innerException);
