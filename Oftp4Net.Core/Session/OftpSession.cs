using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using Oftp4Net.Core.Protocol;
using Oftp4Net.Core.Protocol.Commands;

namespace Oftp4Net.Core.Session;

/// <summary>
/// Runs one OFTP2 session (RFC 5024) over an established connection, in either the initiator or responder role.
/// </summary>
/// <remarks>
/// The session follows the speaker / listener model: only the speaker sends SFID, DATA, EFID, EERP and NERP.
/// A speaker with nothing (more) to send hands over with CD. When a side becomes the speaker right after
/// receiving CD and has nothing to send, it ends the session with ESID.
/// File level security (signing, compression, encryption) is not handled here: the session only carries the
/// attributes of SFID, the application decides what it accepts and processes the content. Secure authentication
/// is driven here, the encryption of the challenge is done by the application.
/// Buffer compression and restart of interrupted transfers are used when both sides offer them in SSID.
/// </remarks>
public sealed class OftpSession
{
    private readonly OftpTransport _transport;
    private readonly OftpSessionOptions _options;
    private readonly OftpSessionHandler _handler;
    private readonly ILogger _logger;
    private readonly MemoryStream _decodeBuffer = new();
    private readonly List<int> _recordEnds = [];

    private bool _peerAcceptsFiles = true;
    private CancellationTokenSource? _receiveTimeout;
    private CancellationToken _receiveTimeoutToken;

    public OftpSession(OftpTransport transport, OftpSessionOptions options, OftpSessionHandler handler, ILogger logger)
    {
        options.Validate();
        _transport = transport;
        _options = options;
        _handler = handler;
        _logger = logger;
        // The initiator announces its own capabilities in SSID; for a responder those of the identified partner
        // replace them before its SSID is sent.
        SecureAuthenticationAgreed = options.SecureAuthentication;
        BufferCompressionAgreed = options.BufferCompression;
        RestartAgreed = options.Restart;
        Level = options.ProtocolLevel;
    }

    /// <summary>SSID received from the peer, available once the session has started.</summary>
    public SSID? RemoteSsid { get; private set; }

    /// <summary>Negotiated data exchange buffer size.</summary>
    public int ExchangeBufferSize { get; private set; }

    /// <summary>Negotiated credit.</summary>
    public int Credit { get; private set; }

    /// <summary>State returned by <see cref="OftpSessionHandler.AuthenticateAsync"/>.</summary>
    public object? State { get; private set; }

    /// <summary>Both sides require secure authentication (SSIDAUTH), so the session runs the SECD/AUCH/AURP phase.</summary>
    public bool SecureAuthenticationAgreed { get; private set; }

    /// <summary>Both sides offered buffer compression (SSIDCMPR), so data exchange buffers are compressed when sending.</summary>
    public bool BufferCompressionAgreed { get; private set; }

    /// <summary>Both sides offered restart (SSIDREST), so interrupted transfers may be continued.</summary>
    public bool RestartAgreed { get; private set; }

    /// <summary>
    /// Negotiated protocol release level (SSIDLEV): the lower of the two announced levels. It decides the layout
    /// of the commands and which features may be used at all.
    /// </summary>
    public int Level { get; private set; } = ProtocolLevels.Oftp2;

    /// <summary>Unit of the restart position of an unstructured file (SFIDREST), see RFC 5024, section 5.3.3.</summary>
    public const int RestartBlockSize = 1024;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await StartAsync(cancellationToken);

            var speaker = _options.Role == OftpRole.Initiator;
            var changeDirectionReceived = false;

            while (true)
            {
                if (speaker)
                {
                    var sentSomething = await SpeakAsync(cancellationToken);
                    if (!sentSomething && changeDirectionReceived)
                    {
                        await SendAsync(new ESID { ReasonCode = ReasonCodes.NormalTermination }, cancellationToken);
                        return;
                    }

                    await SendAsync(new CD(), cancellationToken);
                    speaker = false;
                }
                else
                {
                    if (!await ListenAsync(cancellationToken))
                        return;

                    speaker = true;
                    changeDirectionReceived = true;
                }
            }
        }
        catch (OftpProtocolException ex)
        {
            _logger.LogWarning("OFTP protocol error {ReasonCode}: {Message}", ex.ReasonCode, ex.Message);
            await TrySendAsync(new ESID { ReasonCode = ex.ReasonCode, ReasonText = Truncate(ex.Message, 999) });
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TrySendAsync(new ESID
            {
                ReasonCode = ReasonCodes.LocalSiteEmergencyCloseDown,
                ReasonText = "Service is shutting down."
            });
            throw;
        }
        finally
        {
            _receiveTimeout?.Dispose();
            _receiveTimeout = null;
        }
    }

    #region Session start

    private async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.Role == OftpRole.Initiator)
        {
            await ReceiveAsync<SSRM>(cancellationToken);

            await SendAsync(CreateSsid(_options.LocalCode, _options.LocalPassword, _options.ExchangeBufferSize, _options.Credit),
                cancellationToken);

            var remote = await ReceiveAsync<SSID>(cancellationToken);
            await AcceptRemoteSsidAsync(remote, cancellationToken);
        }
        else
        {
            await SendAsync(new SSRM(), cancellationToken);

            var remote = await ReceiveAsync<SSID>(cancellationToken);
            var auth = await AcceptRemoteSsidAsync(remote, cancellationToken);

            // The responder must not propose more than the initiator did.
            await SendAsync(CreateSsid(auth.LocalCode ?? "", auth.LocalPassword ?? "", ExchangeBufferSize, Credit),
                cancellationToken);
        }

        _logger.LogInformation(
            "OFTP {Revision} session started with {Code} as {Role}, buffer size {BufferSize}, credit {Credit}{Features}",
            ProtocolLevels.Name(Level), RemoteSsid!.Code, _options.Role, ExchangeBufferSize, Credit, string.Concat(
                SecureAuthenticationAgreed ? ", secure authentication" : "",
                BufferCompressionAgreed ? ", buffer compression" : "",
                RestartAgreed ? ", restart" : ""));

        if (SecureAuthenticationAgreed)
            await AuthenticateSecurelyAsync(cancellationToken);
    }

    private SSID CreateSsid(string code, string password, int bufferSize, int credit) => new()
    {
        Level = Level,
        Code = code,
        Password = password,
        ExchangeBufferSize = bufferSize,
        Credit = credit,
        SendReceive = _options.SendReceive,
        BufferCompression = BufferCompressionAgreed,
        Restart = RestartAgreed,
        SpecialLogic = false,
        SecureAuthentication = SecureAuthenticationAgreed,
    };

    private async Task<OftpAuthenticationResult> AcceptRemoteSsidAsync(SSID remote, CancellationToken cancellationToken)
    {
        RemoteSsid = remote;

        if (!ProtocolLevels.IsSupported(remote.Level))
            throw new OftpProtocolException(ReasonCodes.ModeOrCapabilitiesIncompatible,
                $"Protocol release level {remote.Level} is not supported.");

        if (_options.Role == OftpRole.Initiator && remote.Level > _options.ProtocolLevel)
            throw new OftpProtocolException(ReasonCodes.ModeOrCapabilitiesIncompatible,
                $"Responder answered with protocol level {remote.Level}, higher than the offered {_options.ProtocolLevel}.");

        // The lower of the two levels is used, and the rest of the session is encoded in its layout.
        // A responder learns the level of the identified partner only with the authentication below.
        Level = Math.Min(_options.ProtocolLevel, remote.Level);

        if (remote.ExchangeBufferSize < OftpSessionOptions.MinExchangeBufferSize)
            throw new OftpProtocolException(ReasonCodes.ExchangeBufferSizeError,
                $"Exchange buffer size {remote.ExchangeBufferSize} is too small.");

        if (remote.Credit < 1)
            throw new OftpProtocolException(ReasonCodes.CommandContainedInvalidData, "Credit must be at least 1.");

        if (_options.Role == OftpRole.Initiator &&
            (remote.ExchangeBufferSize > _options.ExchangeBufferSize || remote.Credit > _options.Credit))
            throw new OftpProtocolException(ReasonCodes.ModeOrCapabilitiesIncompatible,
                "Responder proposed a larger buffer size or credit than the initiator.");

        var ourSendReceive = _options.SendReceive;
        if ((ourSendReceive == SendReceiveCapabilities.SendOnly && remote.SendReceive == SendReceiveCapabilities.SendOnly) ||
            (ourSendReceive == SendReceiveCapabilities.ReceiveOnly && remote.SendReceive == SendReceiveCapabilities.ReceiveOnly))
            throw new OftpProtocolException(ReasonCodes.ModeOrCapabilitiesIncompatible,
                $"Send/receive capabilities are incompatible ({ourSendReceive} / {remote.SendReceive}).");

        _peerAcceptsFiles = remote.SendReceive != SendReceiveCapabilities.SendOnly &&
                            ourSendReceive != SendReceiveCapabilities.ReceiveOnly;

        var auth = await _handler.AuthenticateAsync(remote, cancellationToken);
        if (!auth.Success)
            throw new OftpProtocolException(auth.ReasonCode, auth.ReasonText);

        State = auth.State;
        if (auth.ProtocolLevel is { } partnerLevel)
            Level = Math.Min(Level, partnerLevel);

        ExchangeBufferSize = Math.Min(_options.ExchangeBufferSize, remote.ExchangeBufferSize);
        Credit = Math.Min(_options.Credit, remote.Credit);

        // Buffer compression and restart are used only when both sides offer them.
        BufferCompressionAgreed = (auth.BufferCompression ?? _options.BufferCompression) && remote.BufferCompression;
        RestartAgreed = (auth.Restart ?? _options.Restart) && remote.Restart;

        // Secure authentication is not negotiated: both sides have to require it (RFC 5024, section 5.3.2).
        // A responder learns the requirement of the peer together with its identity.
        SecureAuthenticationAgreed = auth.SecureAuthentication ?? _options.SecureAuthentication;
        if (SecureAuthenticationAgreed && !ProtocolLevels.HasOftp2Features(Level))
            throw new OftpProtocolException(ReasonCodes.SecureAuthenticationRequirementsIncompatible,
                $"Secure authentication requires OFTP 2.0, the session runs at level {Level} ({ProtocolLevels.Name(Level)}).");

        if (SecureAuthenticationAgreed != remote.SecureAuthentication)
            throw new OftpProtocolException(ReasonCodes.SecureAuthenticationRequirementsIncompatible,
                SecureAuthenticationAgreed
                    ? $"Secure authentication is required, but {remote.Code} does not use it."
                    : $"{remote.Code} requires secure authentication, which is not configured for it here.");

        return auth;
    }

    /// <summary>
    /// Secure authentication (RFC 5024, section 4.2.3): each side proves that it holds the private key of the
    /// certificate the other side knows. The initiator is challenged first.
    /// </summary>
    private async Task AuthenticateSecurelyAsync(CancellationToken cancellationToken)
    {
        if (_options.Role == OftpRole.Initiator)
        {
            await SendAsync(new SECD(), cancellationToken);
            await AnswerChallengeAsync(cancellationToken);

            await ReceiveAsync<SECD>(cancellationToken);
            await ChallengePeerAsync(cancellationToken);
        }
        else
        {
            await ReceiveAsync<SECD>(cancellationToken);
            await ChallengePeerAsync(cancellationToken);

            await SendAsync(new SECD(), cancellationToken);
            await AnswerChallengeAsync(cancellationToken);
        }

        _logger.LogInformation("Secure authentication with {Code} succeeded", RemoteSsid!.Code);
    }

    /// <summary>Sends a fresh challenge encrypted for the peer and checks that it comes back decrypted.</summary>
    private async Task ChallengePeerAsync(CancellationToken cancellationToken)
    {
        var challenge = SecureAuthentication.CreateChallenge();

        await SendAsync(new AUCH { Challenge = await _handler.EncryptChallengeAsync(challenge, cancellationToken) },
            cancellationToken);

        var response = await ReceiveAsync<AURP>(cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(response.Response, challenge))
            throw new OftpProtocolException(ReasonCodes.InvalidChallengeResponse,
                $"{RemoteSsid!.Code} did not answer the authentication challenge correctly.");
    }

    /// <summary>Decrypts the challenge of the peer and sends it back.</summary>
    private async Task AnswerChallengeAsync(CancellationToken cancellationToken)
    {
        var challenge = await ReceiveAsync<AUCH>(cancellationToken);
        var decrypted = await _handler.DecryptChallengeAsync(challenge.Challenge, cancellationToken);

        if (decrypted.Length != SecureAuthentication.ChallengeLength)
            throw new OftpProtocolException(ReasonCodes.InvalidChallengeResponse,
                $"The decrypted challenge has {decrypted.Length} octets, expected {SecureAuthentication.ChallengeLength}.");

        await SendAsync(new AURP { Response = decrypted }, cancellationToken);
    }

    #endregion

    #region Speaker

    /// <returns><c>true</c> when at least one command was sent.</returns>
    private async Task<bool> SpeakAsync(CancellationToken cancellationToken)
    {
        var sentSomething = false;

        foreach (var response in await _handler.GetPendingEndResponsesAsync(cancellationToken))
        {
            if (response is not (EERP or NERP))
                throw new InvalidOperationException("Only EERP and NERP can be sent as end responses.");

            if (response is NERP && !ProtocolLevels.HasNegativeEndResponse(Level))
            {
                _logger.LogWarning("NERP cannot be sent to {Code}: the session runs at level {Level} ({Revision})",
                    RemoteSsid?.Code, Level, ProtocolLevels.Name(Level));
                continue;
            }

            await SendAsync(response, cancellationToken);
            await ReceiveAsync<RTR>(cancellationToken);
            await _handler.OnEndResponseSentAsync(response, cancellationToken);
            sentSomething = true;
        }

        if (!_peerAcceptsFiles)
            return sentSomething;

        while (await _handler.GetNextFileAsync(cancellationToken) is { } file)
        {
            sentSomething = true;
            var changeDirectionRequested = await SendFileAsync(file, cancellationToken);
            if (changeDirectionRequested)
                break;
        }

        return sentSomething;
    }

    /// <returns><c>true</c> when the listener requested change of direction in EFPA.</returns>
    private async Task<bool> SendFileAsync(OftpOutgoingFile file, CancellationToken cancellationToken)
    {
        // File level security, the signed end response and the description exist only in OFTP 2.0.
        if (!ProtocolLevels.HasOftp2Features(Level) &&
            (file.SecurityLevel != SecurityLevels.None ||
             file.Compression != FileCompressionAlgorithms.None ||
             file.Enveloping != FileEnvelopingFormats.None ||
             file.SignedEerpRequested))
            throw new OftpProtocolException(ReasonCodes.ModeOrCapabilitiesIncompatible,
                $"{file.DatasetName} uses file level security, which requires OFTP 2.0; " +
                $"the session with {RemoteSsid?.Code} runs at level {Level} ({ProtocolLevels.Name(Level)}).");

        file.SentDate = ProtocolLevels.Date(file.Date, Level);
        file.SentTime = ProtocolLevels.Time(file.Time, Level);

        await using var content = await file.OpenAsync(cancellationToken);
        var sizeInBlocks = content.CanSeek ? ToBlocks(content.Length) : 0;
        var originalSizeInBlocks = file.OriginalSize is { } originalSize ? ToBlocks(originalSize) : sizeInBlocks;

        await SendAsync(new SFID
        {
            DatasetName = file.DatasetName,
            Date = file.Date,
            Time = file.Time,
            UserData = file.UserData,
            Destination = file.Destination,
            Originator = file.Originator,
            Format = file.Format,
            MaxRecordSize = file.MaxRecordSize,
            FileSize = sizeInBlocks,
            OriginalFileSize = originalSizeInBlocks,
            // The restart position of a record structured file is a record number, which we do not support;
            // such files always start from the beginning.
            RestartPosition = RestartAgreed && !FileFormats.IsRecordStructured(file.Format)
                ? file.RestartPosition
                : 0,
            SecurityLevel = file.SecurityLevel,
            CipherSuite = file.CipherSuite,
            Compression = file.Compression,
            Enveloping = file.Enveloping,
            SignedEerpRequested = file.SignedEerpRequested,
            Description = file.Description,
        }, cancellationToken);

        var startAnswer = await ReceiveAsync(cancellationToken);
        switch (startAnswer)
        {
            case SFNA sfna:
                _logger.LogWarning("File {DatasetName} refused by peer (SFNA {ReasonCode}: {ReasonText})",
                    file.DatasetName, sfna.ReasonCode, sfna.ReasonText);
                await _handler.OnFileRefusedAsync(file, OftpAnswer.Reject(sfna.ReasonCode, sfna.ReasonText, sfna.RetryLater),
                    cancellationToken);
                return false;
            case SFPA sfpa when sfpa.AnswerCount > (RestartAgreed && !FileFormats.IsRecordStructured(file.Format)
                ? file.RestartPosition
                : 0):
                throw new OftpProtocolException(ReasonCodes.ProtocolViolation,
                    $"SFPA answer count {sfpa.AnswerCount} is higher than the offered restart position {file.RestartPosition}.");
            case SFPA sfpa:
                file.RestartedFrom = sfpa.AnswerCount;
                break;
            default:
                throw Unexpected(startAnswer, "SFPA or SFNA");
        }

        // The listener keeps what it already has; the rest of the content follows (RFC 5024, section 4.3.3).
        var skipped = file.RestartedFrom * RestartBlockSize;
        if (skipped > 0)
        {
            _logger.LogInformation("File {DatasetName} is restarted at block {Block} ({Bytes} octets already received)",
                file.DatasetName, file.RestartedFrom, skipped);
            await SkipAsync(content, skipped, cancellationToken);
        }

        // A signed, compressed or encrypted file has no discernable record boundaries any more, so it is
        // transferred as unstructured whatever SFIDFMT says (RFC 5024, section 5.3.3).
        var secured = IsSecured(file.SecurityLevel, file.Compression, file.Enveloping);
        var reader = new VirtualFileReader(content,
            secured ? FileFormats.Unstructured : file.Format, file.MaxRecordSize);
        var builder = new DataBufferBuilder(ExchangeBufferSize, BufferCompressionAgreed);
        var credit = Credit;
        // The unit count of EFID is the size of the whole file, even for a restarted transfer.
        var unitCount = skipped;
        file.BytesSent = skipped;

        while (true)
        {
            var (data, endOfRecord, endOfFile) = await reader.ReadAsync(cancellationToken);
            if (endOfFile)
                break;

            var offset = 0;
            while (true)
            {
                offset += builder.Append(data.Span[offset..], endOfRecord, out var completed);
                if (completed)
                    break;

                // The buffer is full, the rest of the record goes into the next one.
                await SendBufferAsync();
            }

            unitCount += data.Length;
            file.BytesSent = unitCount;
        }

        if (!builder.IsEmpty)
            await SendBufferAsync();

        await SendAsync(new EFID
        {
            // Only fixed and variable files have a record count, the others send zeros.
            RecordCount = file.RecordCount ?? reader.Records,
            UnitCount = unitCount,
        }, cancellationToken);

        async Task SendBufferAsync()
        {
            if (credit == 0)
            {
                await ReceiveAsync<CDT>(cancellationToken);
                credit = Credit;
            }

            await _transport.WriteAsync(builder.ToData().Encode(Level), cancellationToken);
            credit--;
        }

        while (true)
        {
            var endAnswer = await ReceiveAsync(cancellationToken);
            switch (endAnswer)
            {
                case CDT:
                    // Credit exhausted by the last DATA buffer; the listener granted a new window we no longer need.
                    continue;
                case EFNA efna:
                    _logger.LogWarning("File {DatasetName} refused by peer (EFNA {ReasonCode}: {ReasonText})",
                        file.DatasetName, efna.ReasonCode, efna.ReasonText);
                    await _handler.OnFileRefusedAsync(file, OftpAnswer.Reject(efna.ReasonCode, efna.ReasonText), cancellationToken);
                    return false;
                case EFPA efpa:
                    _logger.LogInformation("File {DatasetName} sent to {Destination}, {Bytes} bytes total",
                        file.DatasetName, file.Destination, unitCount);
                    await _handler.OnFileSentAsync(file, cancellationToken);
                    return efpa.ChangeDirection;
                default:
                    throw Unexpected(endAnswer, "EFPA or EFNA");
            }
        }
    }

    #endregion

    #region Listener

    /// <returns><c>true</c> when the peer handed over with CD, <c>false</c> when it ended the session.</returns>
    private async Task<bool> ListenAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var command = await ReceiveAsync(cancellationToken);
            switch (command)
            {
                case CD:
                    return true;
                case ESID esid:
                    ThrowIfAbnormal(esid);
                    _logger.LogInformation("OFTP session ended by peer");
                    return false;
                case SFID sfid:
                    await ReceiveFileAsync(sfid, cancellationToken);
                    break;
                case EERP or NERP:
                    await _handler.OnEndResponseReceivedAsync(command, cancellationToken);
                    await SendAsync(new RTR(), cancellationToken);
                    break;
                default:
                    throw Unexpected(command, "SFID, EERP, NERP, CD or ESID");
            }
        }
    }

    private async Task ReceiveFileAsync(SFID sfid, CancellationToken cancellationToken)
    {
        var decision = await _handler.OnStartFileAsync(sfid, cancellationToken);

        if (decision.Destination is null)
        {
            var answer = decision.Answer;
            _logger.LogWarning("Refusing file {DatasetName} from {Originator}: {Answer}", sfid.DatasetName, sfid.Originator, answer);
            await _handler.OnStartFileRefusedAsync(sfid, answer, cancellationToken);
            await SendAsync(new SFNA { ReasonCode = answer.ReasonCode, ReasonText = answer.ReasonText, RetryLater = answer.RetryLater },
                cancellationToken);
            return;
        }

        var restartPosition = RestartAgreed && !FileFormats.IsRecordStructured(sfid.Format) ? decision.RestartPosition : 0;
        if (restartPosition > sfid.RestartPosition)
            throw new InvalidOperationException(
                $"The restart position {restartPosition} is higher than the position {sfid.RestartPosition} offered by the speaker.");

        var file = new OftpIncomingFile { Header = sfid, State = decision.State, RestartPosition = restartPosition };
        var destination = decision.Destination;
        var securedContent = IsSecured(sfid.SecurityLevel, sfid.Compression, sfid.Enveloping);
        var writer = new VirtualFileWriter(destination,
            securedContent ? FileFormats.Unstructured : sfid.Format, sfid.MaxRecordSize);
        EFID efid;

        try
        {
            // The answer count tells the speaker how much of the file we keep from an interrupted transfer.
            await SendAsync(new SFPA { AnswerCount = restartPosition }, cancellationToken);

            if (restartPosition > 0)
                _logger.LogInformation("File {DatasetName} from {Originator} continues at block {Block}",
                    sfid.DatasetName, sfid.Originator, restartPosition);

            var credit = Credit;
            while (true)
            {
                var command = await ReceiveAsync(cancellationToken);
                if (command is EFID end)
                {
                    efid = end;
                    break;
                }

                if (command is ESID esid)
                {
                    ThrowIfAbnormal(esid);
                    throw new OftpSessionAbortedException(esid.ReasonCode, "Session ended during file transfer.");
                }

                if (command is not DATA data)
                    throw Unexpected(command, "DATA or EFID");

                if (credit == 0)
                    throw new OftpProtocolException(ReasonCodes.ProtocolViolation, "DATA received without credit.");

                _decodeBuffer.SetLength(0);
                _recordEnds.Clear();
                var length = data.DecodeTo(_decodeBuffer, _recordEnds);
                await writer.WriteAsync(_decodeBuffer.GetBuffer().AsMemory(0, length), _recordEnds, cancellationToken);
                file.BytesReceived += length;

                if (--credit == 0)
                {
                    await SendAsync(new CDT(), cancellationToken);
                    credit = Credit;
                }
            }

            await destination.FlushAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            await destination.DisposeAsync();
            await _handler.OnFileReceiveFailedAsync(file, ex.Message, CancellationToken.None);
            throw;
        }

        await destination.DisposeAsync();

        if (efid.UnitCount != file.TotalBytes)
        {
            var reason = $"Unit count {efid.UnitCount} does not match {file.TotalBytes} received octets.";
            await _handler.OnFileReceiveFailedAsync(file, reason, cancellationToken);
            await SendAsync(new EFNA { ReasonCode = AnswerReasonCodes.InvalidByteCount, ReasonText = reason }, cancellationToken);
            return;
        }

        // Fixed and variable files carry the number of records they consist of; a secured file is counted by
        // the application after it is unpacked, not here.
        file.Records = writer.Records;
        if (FileFormats.IsRecordStructured(sfid.Format) && !securedContent && efid.RecordCount != writer.Records)
        {
            var reason = $"Record count {efid.RecordCount} does not match {writer.Records} received records.";
            await _handler.OnFileReceiveFailedAsync(file, reason, cancellationToken);
            await SendAsync(new EFNA { ReasonCode = AnswerReasonCodes.InvalidRecordCount, ReasonText = reason }, cancellationToken);
            return;
        }

        var result = await _handler.OnFileReceivedAsync(file, cancellationToken);
        if (!result.Accepted)
        {
            await SendAsync(new EFNA { ReasonCode = result.ReasonCode, ReasonText = result.ReasonText }, cancellationToken);
            return;
        }

        _logger.LogInformation("File {DatasetName} received from {Originator}, {Bytes} bytes total",
            sfid.DatasetName, sfid.Originator, file.TotalBytes);
        await SendAsync(new EFPA { ChangeDirection = false }, cancellationToken);
    }

    #endregion

    private static bool IsSecured(string securityLevel, string compression, string enveloping) =>
        securityLevel != SecurityLevels.None ||
        compression != FileCompressionAlgorithms.None ||
        enveloping != FileEnvelopingFormats.None;

    /// <summary>Skips the part of the content the peer already has, by seeking when the stream allows it.</summary>
    private static async Task SkipAsync(Stream content, long count, CancellationToken cancellationToken)
    {
        if (content.CanSeek)
        {
            content.Seek(count, SeekOrigin.Current);
            return;
        }

        var buffer = new byte[81920];
        while (count > 0)
        {
            var length = await content.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, count)), cancellationToken);
            if (length == 0)
                throw new OftpProtocolException(ReasonCodes.CommandContainedInvalidData,
                    "The content is shorter than the restart position accepted by the peer.");
            count -= length;
        }
    }

    #region Transport helpers

    private async Task SendAsync(OftpCommand command, CancellationToken cancellationToken)
    {
        _logger.LogDebug("OFTP >> {Command}", command);
        await _transport.WriteAsync(command.Encode(Level), cancellationToken);
    }

    private async Task TrySendAsync(OftpCommand command)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await SendAsync(command, cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not send {Command}", command.Name);
        }
    }

    private async Task<OftpCommand> ReceiveAsync(CancellationToken cancellationToken)
    {
        var timeout = RenewReceiveTimeout(cancellationToken);

        byte[] buffer;
        try
        {
            buffer = await _transport.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OftpProtocolException(ReasonCodes.TimeOut,
                $"No command received within {_options.ResponseTimeout.TotalSeconds:0} seconds.");
        }

        OftpCommand command;
        try
        {
            command = OftpCommand.Decode(buffer, Level);
        }
        catch (OftpDecodingException ex)
        {
            throw new OftpProtocolException(ReasonCodes.CommandContainedInvalidData, ex.Message);
        }

        if (command is DATA)
            _logger.LogTrace("OFTP << {Command}", command);
        else
            _logger.LogDebug("OFTP << {Command}", command);

        // SSIDSDEB limits Data Exchange Buffers only; commands like SFID may be longer than the minimum of 128.
        if (command is DATA && buffer.Length > ExchangeBufferSize)
            throw new OftpProtocolException(ReasonCodes.ExchangeBufferSizeError,
                $"Exchange buffer of {buffer.Length} octets exceeds negotiated size {ExchangeBufferSize}.");

        return command;
    }

    /// <summary>
    /// Starts the response timeout for the next receive. The source is reused as long as it did not fire, so a
    /// transfer does not allocate a linked source and a timer for every DATA buffer.
    /// </summary>
    private CancellationTokenSource RenewReceiveTimeout(CancellationToken cancellationToken)
    {
        if (_receiveTimeout is null || _receiveTimeoutToken != cancellationToken || !_receiveTimeout.TryReset())
        {
            _receiveTimeout?.Dispose();
            _receiveTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _receiveTimeoutToken = cancellationToken;
        }

        _receiveTimeout.CancelAfter(_options.ResponseTimeout);
        return _receiveTimeout;
    }

    private async Task<T> ReceiveAsync<T>(CancellationToken cancellationToken) where T : OftpCommand
    {
        var command = await ReceiveAsync(cancellationToken);
        if (command is T expected)
            return expected;

        if (command is ESID esid)
            throw new OftpSessionAbortedException(esid.ReasonCode, esid.ReasonText);

        throw Unexpected(command, typeof(T).Name);
    }

    private static void ThrowIfAbnormal(ESID esid)
    {
        if (esid.ReasonCode != ReasonCodes.NormalTermination)
            throw new OftpSessionAbortedException(esid.ReasonCode, esid.ReasonText);
    }

    private static OftpProtocolException Unexpected(OftpCommand command, string expected) =>
        new(ReasonCodes.ProtocolViolation, $"Unexpected command {command.Name}, expected {expected}.");

    private static long ToBlocks(long sizeInBytes) => (sizeInBytes + 1023) / 1024;

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];

    #endregion
}
