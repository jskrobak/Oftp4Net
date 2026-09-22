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

    public static OftpAuthenticationResult Accept(string? localCode = null, string? localPassword = null, object? state = null) =>
        new() { Success = true, LocalCode = localCode, LocalPassword = localPassword, State = state };

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

    public static OftpStartFileDecision Accept(Stream destination, object? state = null) =>
        new() { Destination = destination, State = state };

    public static OftpStartFileDecision Reject(string reasonCode, string reasonText, bool retryLater = false) =>
        new() { Answer = OftpAnswer.Reject(reasonCode, reasonText, retryLater) };
}

public sealed class OftpIncomingFile
{
    public required SFID Header { get; init; }
    public object? State { get; init; }
    public long BytesReceived { get; internal set; }
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

    /// <summary>Opens the content of the file. The session disposes the stream.</summary>
    public required Func<CancellationToken, ValueTask<Stream>> OpenAsync { get; init; }

    /// <summary>Arbitrary application state (e.g. a queue item id).</summary>
    public object? State { get; init; }

    /// <summary>Creates date and time stamps for a new virtual file.</summary>
    public static (string Date, string Time) CreateTimestamp(DateTime timestamp) =>
        (timestamp.ToString("yyyyMMdd"), timestamp.ToString("HHmmssffff"));
}
