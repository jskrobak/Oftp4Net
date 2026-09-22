using Microsoft.Extensions.Logging;
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
/// Not supported: secure authentication, restart, buffer compression when sending, file level security
/// (CMS encryption, signing, compression) and signed EERP.
/// </remarks>
public sealed class OftpSession
{
    private readonly OftpTransport _transport;
    private readonly OftpSessionOptions _options;
    private readonly OftpSessionHandler _handler;
    private readonly ILogger _logger;
    private readonly MemoryStream _decodeBuffer = new();

    private bool _peerAcceptsFiles = true;

    public OftpSession(OftpTransport transport, OftpSessionOptions options, OftpSessionHandler handler, ILogger logger)
    {
        options.Validate();
        _transport = transport;
        _options = options;
        _handler = handler;
        _logger = logger;
    }

    /// <summary>SSID received from the peer, available once the session has started.</summary>
    public SSID? RemoteSsid { get; private set; }

    /// <summary>Negotiated data exchange buffer size.</summary>
    public int ExchangeBufferSize { get; private set; }

    /// <summary>Negotiated credit.</summary>
    public int Credit { get; private set; }

    /// <summary>State returned by <see cref="OftpSessionHandler.AuthenticateAsync"/>.</summary>
    public object? State { get; private set; }

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
            "OFTP session started with {Code} as {Role}, buffer size {BufferSize}, credit {Credit}",
            RemoteSsid!.Code, _options.Role, ExchangeBufferSize, Credit);
    }

    private SSID CreateSsid(string code, string password, int bufferSize, int credit) => new()
    {
        Code = code,
        Password = password,
        ExchangeBufferSize = bufferSize,
        Credit = credit,
        SendReceive = _options.SendReceive,
        BufferCompression = false,
        Restart = false,
        SpecialLogic = false,
        SecureAuthentication = false,
    };

    private async Task<OftpAuthenticationResult> AcceptRemoteSsidAsync(SSID remote, CancellationToken cancellationToken)
    {
        RemoteSsid = remote;

        if (remote.Level != SSID.Oftp2Level)
            throw new OftpProtocolException(ReasonCodes.ModeOrCapabilitiesIncompatible,
                $"Protocol release level {remote.Level} is not supported, only OFTP 2.0 (5).");

        if (remote.SecureAuthentication)
            throw new OftpProtocolException(ReasonCodes.SecureAuthenticationRequirementsIncompatible,
                "Secure authentication is not supported.");

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
        ExchangeBufferSize = Math.Min(_options.ExchangeBufferSize, remote.ExchangeBufferSize);
        Credit = Math.Min(_options.Credit, remote.Credit);
        return auth;
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
        await using var content = await file.OpenAsync(cancellationToken);
        var sizeInBlocks = content.CanSeek ? (content.Length + 1023) / 1024 : 0;

        await SendAsync(new SFID
        {
            DatasetName = file.DatasetName,
            Date = file.Date,
            Time = file.Time,
            UserData = file.UserData,
            Destination = file.Destination,
            Originator = file.Originator,
            Format = file.Format,
            MaxRecordSize = 0,
            FileSize = sizeInBlocks,
            OriginalFileSize = sizeInBlocks,
            RestartPosition = 0,
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
            case SFPA { AnswerCount: not 0 } sfpa:
                throw new OftpProtocolException(ReasonCodes.ProtocolViolation,
                    $"SFPA answer count {sfpa.AnswerCount} does not match requested restart position 0.");
            case SFPA:
                break;
            default:
                throw Unexpected(startAnswer, "SFPA or SFNA");
        }

        var payload = new byte[DATA.MaxPayloadLength(ExchangeBufferSize)];
        var credit = Credit;
        long unitCount = 0;

        while (true)
        {
            var length = await content.ReadAtLeastAsync(payload, payload.Length, throwOnEndOfStream: false, cancellationToken);
            if (length == 0)
                break;

            if (credit == 0)
            {
                await ReceiveAsync<CDT>(cancellationToken);
                credit = Credit;
            }

            await _transport.WriteAsync(DATA.FromPayload(payload.AsSpan(0, length)).Encode(), cancellationToken);
            credit--;
            unitCount += length;
        }

        await SendAsync(new EFID { RecordCount = 0, UnitCount = unitCount }, cancellationToken);

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
                    _logger.LogInformation("File {DatasetName} sent to {Destination}, {Bytes} bytes",
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
        var refusal = CheckSupported(sfid);
        var decision = refusal is null
            ? await _handler.OnStartFileAsync(sfid, cancellationToken)
            : OftpStartFileDecision.Reject(refusal.ReasonCode, refusal.ReasonText);

        if (decision.Destination is null)
        {
            var answer = decision.Answer;
            _logger.LogWarning("Refusing file {DatasetName} from {Originator}: {Answer}", sfid.DatasetName, sfid.Originator, answer);
            await SendAsync(new SFNA { ReasonCode = answer.ReasonCode, ReasonText = answer.ReasonText, RetryLater = answer.RetryLater },
                cancellationToken);
            return;
        }

        var file = new OftpIncomingFile { Header = sfid, State = decision.State };
        var destination = decision.Destination;
        EFID efid;

        try
        {
            // Restart is not supported, the transfer always starts from the beginning.
            await SendAsync(new SFPA { AnswerCount = 0 }, cancellationToken);

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
                var length = data.DecodeTo(_decodeBuffer);
                await destination.WriteAsync(_decodeBuffer.GetBuffer().AsMemory(0, length), cancellationToken);
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

        if (efid.UnitCount != file.BytesReceived)
        {
            var reason = $"Unit count {efid.UnitCount} does not match {file.BytesReceived} received octets.";
            await _handler.OnFileReceiveFailedAsync(file, reason, cancellationToken);
            await SendAsync(new EFNA { ReasonCode = AnswerReasonCodes.InvalidByteCount, ReasonText = reason }, cancellationToken);
            return;
        }

        var result = await _handler.OnFileReceivedAsync(file, cancellationToken);
        if (!result.Accepted)
        {
            await SendAsync(new EFNA { ReasonCode = result.ReasonCode, ReasonText = result.ReasonText }, cancellationToken);
            return;
        }

        _logger.LogInformation("File {DatasetName} received from {Originator}, {Bytes} bytes",
            sfid.DatasetName, sfid.Originator, file.BytesReceived);
        await SendAsync(new EFPA { ChangeDirection = false }, cancellationToken);
    }

    private static OftpAnswer? CheckSupported(SFID sfid)
    {
        if (sfid.Format is not (FileFormats.Unstructured or FileFormats.Text or FileFormats.Fixed or FileFormats.Variable))
            return OftpAnswer.Reject(AnswerReasonCodes.StorageRecordFormatNotSupported, $"File format '{sfid.Format}' is not supported.");
        if (sfid.SecurityLevel is SecurityLevels.Encrypted or SecurityLevels.EncryptedAndSigned)
            return OftpAnswer.Reject(AnswerReasonCodes.EncryptedFileNotAllowed, "Encrypted files are not supported.");
        if (sfid.SecurityLevel == SecurityLevels.Signed)
            return OftpAnswer.Reject(AnswerReasonCodes.SignedFileNotAllowed, "Signed files are not supported.");
        if (sfid.Compression != FileCompressionAlgorithms.None)
            return OftpAnswer.Reject(AnswerReasonCodes.CompressionNotAllowed, "Compressed files are not supported.");
        if (sfid.Enveloping != FileEnvelopingFormats.None)
            return OftpAnswer.Reject(AnswerReasonCodes.UnspecifiedReason, "Enveloped files are not supported.");
        if (sfid.SignedEerpRequested)
            return OftpAnswer.Reject(AnswerReasonCodes.UnspecifiedReason, "Signed EERP is not supported.");
        return null;
    }

    #endregion

    #region Transport helpers

    private async Task SendAsync(OftpCommand command, CancellationToken cancellationToken)
    {
        _logger.LogDebug("OFTP >> {Command}", command);
        await _transport.WriteAsync(command.Encode(), cancellationToken);
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
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.ResponseTimeout);

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
            command = OftpCommand.Decode(buffer);
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

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];

    #endregion
}
