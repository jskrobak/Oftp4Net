using System.Collections.Concurrent;
using Oftp4Net.Core.Protocol;
using Oftp4Net.Core.Protocol.Commands;
using Oftp4Net.Core.Session;

namespace Oftp4Net.Core.Tests;

/// <summary>In-memory handler: sends queued files, stores received files and answers them with EERP.</summary>
internal sealed class TestSessionHandler(string localCode, string localPassword, string remoteCode, string remotePassword)
    : OftpSessionHandler
{
    private readonly Queue<OftpOutgoingFile> _outgoing = new();
    private readonly List<OftpCommand> _pendingResponses = [];

    public string LocalSfid => localCode;
    public ConcurrentDictionary<string, byte[]> ReceivedFiles { get; } = new();
    public ConcurrentDictionary<string, SFID> ReceivedHeaders { get; } = new();
    public List<string> SentFiles { get; } = [];
    public List<(string DatasetName, OftpAnswer Answer)> RefusedFiles { get; } = [];
    public List<OftpCommand> ReceivedEndResponses { get; } = [];
    public List<string> RefuseDatasetNames { get; } = [];

    public void Enqueue(string datasetName, byte[] content, string destination,
        string securityLevel = SecurityLevels.None, string cipherSuite = CipherSuites.None,
        string compression = FileCompressionAlgorithms.None, string enveloping = FileEnvelopingFormats.None,
        bool signedEerpRequested = false, long? originalSize = null, long restartPosition = 0)
    {
        var (date, time) = OftpOutgoingFile.CreateTimestamp(DateTime.Now);
        _outgoing.Enqueue(new OftpOutgoingFile
        {
            DatasetName = datasetName,
            Originator = LocalSfid,
            Destination = destination,
            Date = date,
            Time = time,
            SecurityLevel = securityLevel,
            CipherSuite = cipherSuite,
            Compression = compression,
            Enveloping = enveloping,
            SignedEerpRequested = signedEerpRequested,
            OriginalSize = originalSize,
            RestartPosition = restartPosition,
            OpenAsync = _ => ValueTask.FromResult<Stream>(new MemoryStream(content)),
        });
    }

    /// <summary>Beginning of a file left over from an interrupted transfer, by dataset name.</summary>
    public Dictionary<string, byte[]> PartiallyReceived { get; } = [];

    /// <summary>Files sent in this session, with the position they were restarted from.</summary>
    public List<(string DatasetName, long RestartedFrom)> SentFilesWithRestart { get; } = [];

    /// <summary>Secure authentication is required with the peer (SSIDAUTH answered by a responder).</summary>
    public bool? SecureAuthentication { get; set; }

    /// <summary>Challenges we encrypted for the peer and challenges we decrypted, in the order they were handled.</summary>
    public List<byte[]> EncryptedChallenges { get; } = [];
    public List<byte[]> DecryptedChallenges { get; } = [];

    /// <summary>Breaks the answer to the peer's challenge, as a peer without the right private key would.</summary>
    public bool AnswerChallengeWrongly { get; set; }

    /// <summary>Stands in for CMS enveloping: the challenge is only masked, which is enough to drive the protocol.</summary>
    private static byte[] Mask(byte[] value) => value.Select(b => (byte)(b ^ 0x5A)).ToArray();

    public override ValueTask<byte[]> EncryptChallengeAsync(byte[] challenge, CancellationToken cancellationToken)
    {
        EncryptedChallenges.Add(challenge);
        return ValueTask.FromResult(Mask(challenge));
    }

    public override ValueTask<byte[]> DecryptChallengeAsync(byte[] challenge, CancellationToken cancellationToken)
    {
        var decrypted = Mask(challenge);
        DecryptedChallenges.Add(decrypted);

        if (AnswerChallengeWrongly)
            decrypted[0] ^= 0xFF;

        return ValueTask.FromResult(decrypted);
    }

    public override ValueTask<OftpAuthenticationResult> AuthenticateAsync(SSID remote, CancellationToken cancellationToken)
    {
        if (remote.Code != remoteCode)
            return ValueTask.FromResult(OftpAuthenticationResult.Reject(ReasonCodes.UserCodeNotKnown, "Unknown code"));
        if (remote.Password != remotePassword)
            return ValueTask.FromResult(OftpAuthenticationResult.Reject(ReasonCodes.InvalidPassword, "Invalid password"));

        return ValueTask.FromResult(OftpAuthenticationResult.Accept(localCode, localPassword,
            secureAuthentication: SecureAuthentication));
    }

    public override ValueTask<OftpOutgoingFile?> GetNextFileAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(_outgoing.TryDequeue(out var file) ? file : null);

    public override ValueTask OnFileSentAsync(OftpOutgoingFile file, CancellationToken cancellationToken)
    {
        SentFiles.Add(file.DatasetName);
        SentFilesWithRestart.Add((file.DatasetName, file.RestartedFrom));
        return ValueTask.CompletedTask;
    }

    public override ValueTask OnFileRefusedAsync(OftpOutgoingFile file, OftpAnswer answer, CancellationToken cancellationToken)
    {
        RefusedFiles.Add((file.DatasetName, answer));
        return ValueTask.CompletedTask;
    }

    public override ValueTask<OftpStartFileDecision> OnStartFileAsync(SFID header, CancellationToken cancellationToken)
    {
        if (RefuseDatasetNames.Contains(header.DatasetName))
            return ValueTask.FromResult(OftpStartFileDecision.Reject(AnswerReasonCodes.DuplicateFile, "Duplicate", retryLater: true));

        ReceivedHeaders[header.DatasetName] = header;
        var stream = new CapturingStream();

        // Continue an interrupted transfer: keep the complete blocks we have and ask for the rest.
        var restartPosition = 0L;
        if (PartiallyReceived.TryGetValue(header.DatasetName, out var partial))
        {
            restartPosition = Math.Min(header.RestartPosition, partial.Length / OftpSession.RestartBlockSize);
            stream.Write(partial, 0, (int)(restartPosition * OftpSession.RestartBlockSize));
        }

        return ValueTask.FromResult(OftpStartFileDecision.Accept(stream, (header, stream), restartPosition));
    }

    /// <summary>Files that are answered with a NERP instead of an EERP, as not deliverable further.</summary>
    public List<string> NotDeliverableDatasetNames { get; } = [];

    public override ValueTask<OftpAnswer> OnFileReceivedAsync(OftpIncomingFile file, CancellationToken cancellationToken)
    {
        // The session disposes the destination stream before calling this; CapturingStream keeps its content.
        var (header, stream) = ((SFID, CapturingStream))file.State!;
        ReceivedFiles[header.DatasetName] = stream.Captured;

        _pendingResponses.Add(NotDeliverableDatasetNames.Contains(header.DatasetName)
            ? new NERP
            {
                DatasetName = header.DatasetName,
                Date = header.Date,
                Time = header.Time,
                Destination = header.Originator,
                Originator = header.Destination,
                Creator = header.Destination,
                ReasonCode = AnswerReasonCodes.InvalidDestination,
                ReasonText = "The final destination does not accept the file.",
            }
            : EERP.For(header));

        return ValueTask.FromResult(OftpAnswer.Accept());
    }

    public override ValueTask<IReadOnlyList<OftpCommand>> GetPendingEndResponsesAsync(CancellationToken cancellationToken)
    {
        var responses = _pendingResponses.ToList();
        _pendingResponses.Clear();
        return ValueTask.FromResult<IReadOnlyList<OftpCommand>>(responses);
    }

    public override ValueTask OnEndResponseReceivedAsync(OftpCommand response, CancellationToken cancellationToken)
    {
        ReceivedEndResponses.Add(response);
        return ValueTask.CompletedTask;
    }

    private sealed class CapturingStream : MemoryStream
    {
        public byte[] Captured { get; private set; } = [];

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                Captured = ToArray();
            base.Dispose(disposing);
        }
    }
}
