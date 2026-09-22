using Oftp4Net.Core.Protocol;
using Oftp4Net.Core.Protocol.Commands;

namespace Oftp4Net.Core.Session;

/// <summary>
/// Application callbacks of an <see cref="OftpSession"/>. The session drives the protocol, the handler decides
/// what to send, where to store received files and who is allowed to connect.
/// </summary>
public abstract class OftpSessionHandler
{
    /// <summary>
    /// Validates the SSID received from the peer. For a responder the result also provides the local
    /// identification sent back in the responder's SSID.
    /// </summary>
    public abstract ValueTask<OftpAuthenticationResult> AuthenticateAsync(SSID remote, CancellationToken cancellationToken);

    /// <summary>
    /// Secure authentication: encrypts a challenge for the peer, normally as a CMS envelope for the peer's
    /// certificate (AUCHCHAL). Throw an <see cref="OftpProtocolException"/> when the peer cannot be challenged.
    /// </summary>
    public virtual ValueTask<byte[]> EncryptChallengeAsync(byte[] challenge, CancellationToken cancellationToken) =>
        throw new OftpProtocolException(ReasonCodes.SecureAuthenticationRequirementsIncompatible,
            "Secure authentication is not supported by the application.");

    /// <summary>
    /// Secure authentication: decrypts a challenge received from the peer with our own private key.
    /// Throw an <see cref="OftpProtocolException"/> when it cannot be decrypted.
    /// </summary>
    public virtual ValueTask<byte[]> DecryptChallengeAsync(byte[] challenge, CancellationToken cancellationToken) =>
        throw new OftpProtocolException(ReasonCodes.SecureAuthenticationRequirementsIncompatible,
            "Secure authentication is not supported by the application.");

    /// <summary>
    /// Returns the next file to send while this side is the speaker, or <c>null</c> when there is nothing to send.
    /// Must not return the same file twice within one session.
    /// </summary>
    public virtual ValueTask<OftpOutgoingFile?> GetNextFileAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<OftpOutgoingFile?>(null);

    /// <summary>The peer confirmed the file with EFPA.</summary>
    public virtual ValueTask OnFileSentAsync(OftpOutgoingFile file, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <summary>The peer refused the file with SFNA or EFNA.</summary>
    public virtual ValueTask OnFileRefusedAsync(OftpOutgoingFile file, OftpAnswer answer, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    /// <summary>
    /// The peer wants to send a file. Accept it by returning a writable destination stream (the session disposes it
    /// when the transfer ends) or refuse it.
    /// </summary>
    public virtual ValueTask<OftpStartFileDecision> OnStartFileAsync(SFID header, CancellationToken cancellationToken) =>
        ValueTask.FromResult(OftpStartFileDecision.Reject(AnswerReasonCodes.FileDirectionRefused, "Receiving files is not supported."));

    /// <summary>
    /// An offered file was refused with SFNA, either by <see cref="OnStartFileAsync"/> or by the session itself
    /// (unsupported format, encryption, compression, …).
    /// </summary>
    public virtual ValueTask OnStartFileRefusedAsync(SFID header, OftpAnswer answer, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    /// <summary>
    /// The complete file was received and its destination stream was closed. Returning a rejection sends EFNA.
    /// </summary>
    public virtual ValueTask<OftpAnswer> OnFileReceivedAsync(OftpIncomingFile file, CancellationToken cancellationToken) =>
        ValueTask.FromResult(OftpAnswer.Accept());

    /// <summary>
    /// Receiving of an accepted file did not complete (byte count mismatch, connection loss, session abort).
    /// The destination stream is already closed.
    /// </summary>
    public virtual ValueTask OnFileReceiveFailedAsync(OftpIncomingFile file, string reason, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    /// <summary>End to end responses (<see cref="EERP"/> or <see cref="NERP"/>) waiting to be sent to the peer.</summary>
    public virtual ValueTask<IReadOnlyList<OftpCommand>> GetPendingEndResponsesAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<OftpCommand>>([]);

    /// <summary>The peer acknowledged an end to end response with RTR.</summary>
    public virtual ValueTask OnEndResponseSentAsync(OftpCommand response, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    /// <summary>An <see cref="EERP"/> or <see cref="NERP"/> was received from the peer.</summary>
    public virtual ValueTask OnEndResponseReceivedAsync(OftpCommand response, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}

public sealed class OftpAuthenticationResult
{
    public bool Success { get; private init; }
    public string ReasonCode { get; private init; } = ReasonCodes.NormalTermination;
    public string ReasonText { get; private init; } = "";

    /// <summary>Responder only: identification code sent in the responder's SSID.</summary>
    public string? LocalCode { get; private init; }

    /// <summary>Responder only: password sent in the responder's SSID.</summary>
    public string? LocalPassword { get; private init; }

    /// <summary>Arbitrary application state, available as <see cref="OftpSession.State"/>.</summary>
    public object? State { get; private init; }

    /// <summary>
    /// Responder only: whether secure authentication is required with the identified peer. When not set, the
    /// requirement from <see cref="OftpSessionOptions.SecureAuthentication"/> is used.
    /// </summary>
    public bool? SecureAuthentication { get; private init; }

    /// <summary>
    /// Responder only: whether buffer compression is offered to the identified peer. When not set,
    /// <see cref="OftpSessionOptions.BufferCompression"/> is used.
    /// </summary>
    public bool? BufferCompression { get; private init; }

    /// <summary>
    /// Responder only: whether restart is offered to the identified peer. When not set,
    /// <see cref="OftpSessionOptions.Restart"/> is used.
    /// </summary>
    public bool? Restart { get; private init; }

    public static OftpAuthenticationResult Accept(string? localCode = null, string? localPassword = null, object? state = null,
        bool? secureAuthentication = null, bool? bufferCompression = null, bool? restart = null) =>
        new()
        {
            Success = true,
            LocalCode = localCode,
            LocalPassword = localPassword,
            State = state,
            SecureAuthentication = secureAuthentication,
            BufferCompression = bufferCompression,
            Restart = restart,
        };

    public static OftpAuthenticationResult Reject(string reasonCode, string reasonText) =>
        new() { Success = false, ReasonCode = reasonCode, ReasonText = reasonText };
}

/// <summary>Positive or negative answer with an OFTP answer reason code.</summary>
public sealed class OftpAnswer
{
    public bool Accepted { get; private init; }
    public string ReasonCode { get; private init; } = "";
    public string ReasonText { get; private init; } = "";
    public bool RetryLater { get; private init; }

    public static OftpAnswer Accept() => new() { Accepted = true };

    public static OftpAnswer Reject(string reasonCode, string reasonText, bool retryLater = false) =>
        new() { Accepted = false, ReasonCode = reasonCode, ReasonText = reasonText, RetryLater = retryLater };

    public override string ToString() => Accepted ? "accepted" : $"refused {ReasonCode}: {ReasonText}";
}

public sealed class OftpStartFileDecision
{
    public Stream? Destination { get; private init; }
    public OftpAnswer Answer { get; private init; } = OftpAnswer.Accept();

    /// <summary>Arbitrary application state, available as <see cref="OftpIncomingFile.State"/>.</summary>
    public object? State { get; private init; }

    /// <summary>
    /// Restart position answered in SFPA: how many complete 1K blocks of the file are already stored and are not
    /// to be sent again. It must not be higher than the position offered by the speaker in SFID and the
    /// destination stream must be positioned behind them.
    /// </summary>
    public long RestartPosition { get; private init; }

    public static OftpStartFileDecision Accept(Stream destination, object? state = null, long restartPosition = 0) =>
        new() { Destination = destination, State = state, RestartPosition = restartPosition };

    public static OftpStartFileDecision Reject(string reasonCode, string reasonText, bool retryLater = false) =>
        new() { Answer = OftpAnswer.Reject(reasonCode, reasonText, retryLater) };
}

public sealed class OftpIncomingFile
{
    public required SFID Header { get; init; }
    public object? State { get; init; }

    /// <summary>Octets received in this transfer, i.e. without the part received before a restart.</summary>
    public long BytesReceived { get; internal set; }

    /// <summary>Position (in 1K blocks) the transfer was restarted from, zero for a complete transfer.</summary>
    public long RestartPosition { get; internal set; }

    /// <summary>Size of the whole file, including the part received before a restart.</summary>
    public long TotalBytes => RestartPosition * OftpSession.RestartBlockSize + BytesReceived;
}

/// <summary>A virtual file to be sent to the peer.</summary>
public sealed class OftpOutgoingFile
{
    public required string DatasetName { get; init; }
    public required string Originator { get; init; }
    public required string Destination { get; init; }

    /// <summary>Virtual file date stamp CCYYMMDD. Together with <see cref="Time"/> it identifies the file in EERP.</summary>
    public required string Date { get; init; }

    /// <summary>Virtual file time stamp HHMMSScccc.</summary>
    public required string Time { get; init; }

    public string UserData { get; init; } = "";
    public string Description { get; init; } = "";
    public string Format { get; init; } = FileFormats.Unstructured;

    /// <summary>Security of the transferred content (SFIDSEC), see <see cref="SecurityLevels"/>.</summary>
    public string SecurityLevel { get; init; } = SecurityLevels.None;

    /// <summary>Cipher suite used for signing, encryption and the requested signed EERP (SFIDCIPH).</summary>
    public string CipherSuite { get; init; } = CipherSuites.None;

    /// <summary>Compression of the transferred content (SFIDCOMP).</summary>
    public string Compression { get; init; } = FileCompressionAlgorithms.None;

    /// <summary>Enveloping format of the transferred content (SFIDENV).</summary>
    public string Enveloping { get; init; } = FileEnvelopingFormats.None;

    /// <summary>Ask the partner to sign the End to End Response of this file (SFIDSIGN).</summary>
    public bool SignedEerpRequested { get; init; }

    /// <summary>
    /// Size of the file before signing, compression and encryption (SFIDOSIZ).
    /// When not set, the size of the transferred content is used.
    /// </summary>
    public long? OriginalSize { get; init; }

    /// <summary>
    /// Restart position offered in SFID: how many complete 1K blocks of this file the peer is believed to have
    /// from an interrupted transfer. Ignored when restart was not agreed for the session.
    /// </summary>
    public long RestartPosition { get; init; }

    /// <summary>Position (in 1K blocks) the transfer really started from, answered by the peer in SFPA.</summary>
    public long RestartedFrom { get; internal set; }

    /// <summary>Octets of the content handed to the transport so far, used to restart an interrupted transfer.</summary>
    public long BytesSent { get; internal set; }

    /// <summary>Opens the content of the file. The session disposes the stream.</summary>
    public required Func<CancellationToken, ValueTask<Stream>> OpenAsync { get; init; }

    /// <summary>Arbitrary application state (e.g. a queue item id).</summary>
    public object? State { get; init; }

    /// <summary>Creates date and time stamps for a new virtual file.</summary>
    public static (string Date, string Time) CreateTimestamp(DateTime timestamp) =>
        (timestamp.ToString("yyyyMMdd"), timestamp.ToString("HHmmssffff"));
}
