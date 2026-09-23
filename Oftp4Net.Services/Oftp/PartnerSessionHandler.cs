using Havit.Data.Patterns.UnitOfWorks;
using Havit.Services.TimeServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Oftp4Net.Core;
using Oftp4Net.Core.Protocol;
using Oftp4Net.Core.Protocol.Commands;
using Oftp4Net.Core.Session;
using Oftp4Net.DataLayer.Repositories;
using Oftp4Net.Domain;
using System.Diagnostics;
using Oftp4Net.Services.Api;
using Oftp4Net.Services.Encodings;
using Oftp4Net.Services.Hooks;
using Oftp4Net.Services.Security;
using Oftp4Net.Services.TransferEvents;

namespace Oftp4Net.Services.Oftp;

/// <summary>
/// Connects an OFTP session with the database: sends queued files of one partner, stores received files
/// and exchanges End to End Responses. Used for both outgoing (initiator) and incoming (responder) sessions.
/// Create one instance per session within its own DI scope.
/// </summary>
public sealed class PartnerSessionHandler : OftpSessionHandler, IDisposable
{
    private readonly ISendQueueItemRepository _sendQueue;
    private readonly IReceivedFileRepository _receivedFiles;
    private readonly IPartnerRepository _partners;
    private readonly IIdentityRepository _identities;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TransferClaims _claims;
    private readonly ITimeService _timeService;
    private readonly GlobalSettings _settings;
    private readonly GlobalSettingsService _settingsService;
    private readonly IHookDispatcher _hooks;
    private readonly ITransferEventLog _events;
    private readonly IWebhookDispatcher _webhooks;
    private readonly ApiTokenService _apiTokens;
    private readonly SessionFileSecurity _fileSecurity;
    private readonly string _remoteEndPoint;
    private readonly Stopwatch _fileStopwatch = new();
    private readonly ILogger _logger;

    private readonly Identity? _identity;
    private readonly Listener? _listener;
    private readonly List<string> _claimed = [];
    private readonly Dictionary<OftpCommand, ReceivedFile> _pendingResponses = new(ReferenceEqualityComparer.Instance);
    private Queue<SendQueueItem>? _outgoing;
    private SendQueueItem? _inFlight;
    private OftpOutgoingFile? _inFlightFile;

    private PartnerSessionHandler(IServiceProvider scopedServices, GlobalSettings settings, ILogger logger,
        Partner? partner, Identity? identity, Listener? listener, string remoteEndPoint)
    {
        _sendQueue = scopedServices.GetRequiredService<ISendQueueItemRepository>();
        _receivedFiles = scopedServices.GetRequiredService<IReceivedFileRepository>();
        _partners = scopedServices.GetRequiredService<IPartnerRepository>();
        _identities = scopedServices.GetRequiredService<IIdentityRepository>();
        _unitOfWork = scopedServices.GetRequiredService<IUnitOfWork>();
        _claims = scopedServices.GetRequiredService<TransferClaims>();
        _timeService = scopedServices.GetRequiredService<ITimeService>();
        _hooks = scopedServices.GetRequiredService<IHookDispatcher>();
        _events = scopedServices.GetRequiredService<ITransferEventLog>();
        _webhooks = scopedServices.GetRequiredService<IWebhookDispatcher>();
        _apiTokens = scopedServices.GetRequiredService<ApiTokenService>();
        _fileSecurity = new SessionFileSecurity(scopedServices.GetRequiredService<ICertificateRepository>(), settings);
        _remoteEndPoint = remoteEndPoint;
        _settings = settings;
        _settingsService = scopedServices.GetRequiredService<GlobalSettingsService>();
        _logger = logger;
        Partner = partner;
        _identity = identity;
        _listener = listener;
    }

    /// <summary>Handler for a session we open to <paramref name="partner"/> under <paramref name="identity"/>.</summary>
    public static PartnerSessionHandler ForInitiator(IServiceProvider scopedServices, GlobalSettings settings, ILogger logger,
        Partner partner, Identity identity) =>
        new(scopedServices, settings, logger, partner, identity, listener: null, $"{partner.Host}:{partner.Port}");

    /// <summary>Handler for a session a partner opened to <paramref name="listener"/>.</summary>
    public static PartnerSessionHandler ForResponder(IServiceProvider scopedServices, GlobalSettings settings, ILogger logger,
        Listener listener, string remoteEndPoint) =>
        new(scopedServices, settings, logger, partner: null, identity: null, listener, remoteEndPoint);

    /// <summary>The partner of the session; for a responder known after authentication.</summary>
    public Partner? Partner { get; private set; }

    public int FilesSent { get; private set; }

    /// <summary>At least one queued file was offered to the peer (SFID sent) in this session.</summary>
    public bool FileTransferStarted { get; private set; }
    public int FilesReceived { get; private set; }

    #region Authentication

    /// <summary>Connection direction: sessions we open are outgoing, sessions partners open are incoming.</summary>
    private TransferEventCategory ConnectionCategory =>
        _listener is null ? TransferEventCategory.Outgoing : TransferEventCategory.Incoming;

    public override async ValueTask<OftpAuthenticationResult> AuthenticateAsync(SSID remote, CancellationToken cancellationToken)
    {
        var result = await AuthenticateCoreAsync(remote, cancellationToken);

        if (result.Success)
        {
            Record(ConnectionCategory, TransferEventType.SessionStarted, TransferEventLevel.Information,
                _listener is null
                    ? $"Session started with {Partner!.Name} ({Partner.SSID})"
                    : $"Session started by {Partner!.Name} ({Partner.SSID}) on listener {_listener.Name}");
        }
        else
        {
            Record(ConnectionCategory, TransferEventType.AuthenticationRejected, TransferEventLevel.Warning,
                $"Authentication of {remote.Code} rejected ({result.ReasonCode}): {result.ReasonText}",
                e => e.PartnerName ??= remote.Code);
        }

        return result;
    }

    private async Task<OftpAuthenticationResult> AuthenticateCoreAsync(SSID remote, CancellationToken cancellationToken)
    {
        if (_listener is null)
        {
            // Initiator: the responder must be the partner we called.
            if (!SameCode(remote.Code, Partner!.SSID))
                return OftpAuthenticationResult.Reject(ReasonCodes.UserCodeNotKnown,
                    $"Unexpected responder {remote.Code}, expected {Partner.SSID}.");

            return CheckPassword(remote, Partner)
                ? OftpAuthenticationResult.Accept()
                : OftpAuthenticationResult.Reject(ReasonCodes.InvalidPassword, "Invalid password.");
        }

        var partner = await _partners.FindBySsidAsync(remote.Code, cancellationToken);
        if (partner is null)
        {
            _logger.LogWarning("Rejected OFTP session from unknown partner {Code}", remote.Code);
            return OftpAuthenticationResult.Reject(ReasonCodes.UserCodeNotKnown, "User code not known.");
        }

        if (!CheckPassword(remote, partner))
        {
            _logger.LogWarning("Rejected OFTP session from {Partner}: invalid password", partner.Name);
            return OftpAuthenticationResult.Reject(ReasonCodes.InvalidPassword, "Invalid password.");
        }

        if (_listener.Identity is null)
        {
            _logger.LogError("Listener {Listener} has no identity configured", _listener.Name);
            return OftpAuthenticationResult.Reject(ReasonCodes.ResourcesNotAvailable, "Listener is not configured.");
        }

        Partner = partner;
        return OftpAuthenticationResult.Accept(_listener.Identity.SSID, _listener.Identity.Password ?? "",
            secureAuthentication: partner.SecureAuthentication,
            bufferCompression: partner.BufferCompression,
            restart: partner.Restart);
    }

    /// <summary>
    /// Secure authentication: encrypts the challenge for the partner's certificate. Without that certificate the
    /// partner cannot be challenged and the session is aborted.
    /// </summary>
    public override async ValueTask<byte[]> EncryptChallengeAsync(byte[] challenge, CancellationToken cancellationToken)
    {
        var certificate = await _fileSecurity.GetPartnerCertificateAsync(Partner!, cancellationToken)
            ?? throw new OftpProtocolException(ReasonCodes.SecureAuthenticationRequirementsIncompatible,
                $"Partner {Partner!.Name} has no certificate configured to authenticate it with.");

        try
        {
            return FileSecurity.EncryptChallenge(challenge, certificate,
                CipherSuite.Get(Partner!.FileCipherSuite) ?? CipherSuite.Default);
        }
        catch (FileSecurityException ex)
        {
            throw new OftpProtocolException(ReasonCodes.SecureAuthenticationRequirementsIncompatible,
                $"The authentication challenge for {Partner!.Name} could not be encrypted: {ex.Message}");
        }
    }

    /// <summary>Secure authentication: decrypts the challenge of the partner with our own private key.</summary>
    public override async ValueTask<byte[]> DecryptChallengeAsync(byte[] challenge, CancellationToken cancellationToken)
    {
        var certificate = await _fileSecurity.GetOwnCertificateAsync(cancellationToken)
            ?? throw new OftpProtocolException(ReasonCodes.SecureAuthenticationRequirementsIncompatible,
                "No certificate for file security is configured (setting FileSecurityCertificateId).");

        try
        {
            return FileSecurity.DecryptChallenge(challenge, certificate);
        }
        catch (FileSecurityException ex)
        {
            throw new OftpProtocolException(ReasonCodes.InvalidChallengeResponse,
                $"The authentication challenge of {Partner!.Name} could not be decrypted: {ex.Message}");
        }
    }

    private static bool CheckPassword(SSID remote, Partner partner) =>
        string.IsNullOrEmpty(partner.Password) || remote.Password == partner.Password;

    private static bool SameCode(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    #endregion

    #region Sending

    public override async ValueTask<OftpOutgoingFile?> GetNextFileAsync(CancellationToken cancellationToken)
    {
        _outgoing ??= new Queue<SendQueueItem>(
            await _sendQueue.GetPendingForPartnerAsync(Partner!.Id, _identity?.Id, cancellationToken));

        while (_outgoing.TryDequeue(out var item))
        {
            var claim = TransferClaims.ForSendQueueItem(item.Id);
            if (!_claims.TryClaim(claim))
                continue;
            _claimed.Add(claim);

            if (!File.Exists(item.FilePath))
            {
                await MarkFailedAsync(item, $"File '{item.FilePath}' does not exist.", retry: false);
                continue;
            }

            // A continued transfer must keep the virtual file date and time: together with the name they identify
            // the file the partner has a part of.
            var (date, time) = item is { RestartPosition: > 0, FileDate: { Length: > 0 } lastDate, FileTime: { Length: > 0 } lastTime }
                ? (lastDate, lastTime)
                : OftpOutgoingFile.CreateTimestamp(_timeService.GetCurrentTime());
            item.FileDate = date;
            item.FileTime = time;

            SecuredContent content;
            try
            {
                content = await SecureAsync(item, cancellationToken);
            }
            catch (FileSecurityException ex)
            {
                _logger.LogError(ex, "Securing {VirtualFileName} for {Partner} failed", item.VirtualFileName, Partner!.Name);
                await MarkFailedAsync(item, ex.Message, retry: false);
                continue;
            }

            _inFlight = item;
            FileTransferStarted = true;
            _fileStopwatch.Restart();

            return _inFlightFile = new OftpOutgoingFile
            {
                DatasetName = item.VirtualFileName,
                Originator = item.Identity.SFID,
                Destination = Partner!.SFID,
                Date = date,
                Time = time,
                Description = item.Description ?? "",
                State = item,
                SecurityLevel = content.Settings.SecurityLevel,
                CipherSuite = content.Settings.Any || item.SignedResponseRequested
                    ? content.Settings.Suite.Code
                    : CipherSuites.None,
                Compression = content.Settings.Compression,
                Enveloping = content.Settings.Enveloping,
                SignedEerpRequested = item.SignedResponseRequested,
                OriginalSize = content.OriginalSize,
                // Secured content is built anew for every attempt (encryption is randomised), so only a file sent
                // as it is stored can be continued where the previous attempt stopped.
                RestartPosition = content.Settings.Any ? 0 : item.RestartPosition,
                OpenAsync = content.OpenAsync,
            };
        }

        return null;
    }

    public override async ValueTask OnFileSentAsync(OftpOutgoingFile file, CancellationToken cancellationToken)
    {
        var item = (SendQueueItem)file.State!;
        item.Status = SendStatus.SENT;
        item.SentDate = _timeService.GetCurrentTime();
        item.LastError = null;
        item.RestartPosition = 0;
        _inFlight = null;
        _inFlightFile = null;
        FilesSent++;
        await SaveAsync(item);
        Record(TransferEventCategory.Outgoing, TransferEventType.FileSent, TransferEventLevel.Information,
            $"{item.VirtualFileName} sent to {Partner?.Name}", e => Describe(e, item, _fileStopwatch.ElapsedMilliseconds));
        _hooks.Dispatch(HookEvent.OnSent, SendHookParameters(item));
        DispatchItemWebhook(item, "file.sent");
    }

    public override async ValueTask OnFileRefusedAsync(OftpOutgoingFile file, OftpAnswer answer, CancellationToken cancellationToken)
    {
        var item = (SendQueueItem)file.State!;
        _inFlight = null;
        _inFlightFile = null;
        // A refused file is discarded by the partner, so the next attempt starts from the beginning.
        item.RestartPosition = 0;
        await MarkFailedAsync(item, $"Refused by partner ({answer.ReasonCode}): {answer.ReasonText}", answer.RetryLater,
            answer.ReasonCode, answer.ReasonText);
    }

    /// <summary>
    /// Records a failed session: the file being transferred (or, when <paramref name="includeUnattempted"/> is set,
    /// all pending files of the partner) will be retried later.
    /// </summary>
    public async Task OnSessionFailedAsync(Exception exception, bool includeUnattempted, CancellationToken cancellationToken)
    {
        if (_inFlight is not null)
        {
            // The partner keeps what it received, so the next attempt offers to continue there.
            _inFlight.RestartPosition = Partner is { Restart: true } && _inFlightFile is { } sent && !IsSecured(sent)
                ? sent.BytesSent / OftpSession.RestartBlockSize
                : 0;

            if (_inFlight.RestartPosition > 0)
                _logger.LogInformation("Transfer of {VirtualFileName} to {Partner} stopped after {Blocks} complete blocks",
                    _inFlight.VirtualFileName, Partner?.Name, _inFlight.RestartPosition);

            await MarkFailedAsync(_inFlight, exception.Message, retry: true);
            _inFlight = null;
            _inFlightFile = null;
        }

        if (!includeUnattempted || Partner is null)
            return;

        _outgoing ??= new Queue<SendQueueItem>(
            await _sendQueue.GetPendingForPartnerAsync(Partner.Id, _identity?.Id, cancellationToken));

        while (_outgoing.TryDequeue(out var item))
        {
            var claim = TransferClaims.ForSendQueueItem(item.Id);
            if (!_claims.TryClaim(claim))
                continue;
            _claimed.Add(claim);
            await MarkFailedAsync(item, exception.Message, retry: true);
        }
    }

    private async Task MarkFailedAsync(SendQueueItem item, string error, bool retry,
        string? reasonCode = null, string? reasonText = null)
    {
        var now = _timeService.GetCurrentTime();
        item.LastError = error;
        item.LastErrorDate = now;
        item.RetryCount++;

        if (retry && item.RetryCount <= _settings.MaxRetryCount)
        {
            // Exponential back-off: 1, 2, 4, ... minutes, at most one hour.
            var delay = TimeSpan.FromMinutes(Math.Min(Math.Pow(2, item.RetryCount - 1), 60));
            item.Status = SendStatus.ERROR;
            item.NextRetry = now.Add(delay);
            _logger.LogWarning("Sending {VirtualFileName} to {Partner} failed, retry {Retry} at {NextRetry}: {Error}",
                item.VirtualFileName, Partner?.Name, item.RetryCount, item.NextRetry, error);
        }
        else
        {
            item.Status = SendStatus.FAILED;
            _logger.LogError("Sending {VirtualFileName} to {Partner} failed permanently: {Error}",
                item.VirtualFileName, Partner?.Name, error);
        }

        await SaveAsync(item);

        Record(TransferEventCategory.Outgoing, TransferEventType.FileSendFailed,
            item.Status == SendStatus.ERROR ? TransferEventLevel.Warning : TransferEventLevel.Error,
            item.Status == SendStatus.ERROR
                ? $"Sending {item.VirtualFileName} failed, retry {item.RetryCount} at {item.NextRetry:g}: {error}"
                : $"Sending {item.VirtualFileName} failed permanently: {error}",
            e => Describe(e, item, null));

        var parameters = SendHookParameters(item);
        parameters["error"] = error;
        parameters["reasonCode"] = reasonCode;
        parameters["reasonText"] = reasonText;
        parameters["willRetry"] = (item.Status == SendStatus.ERROR).ToString().ToLowerInvariant();
        parameters["retryCount"] = item.RetryCount.ToString();
        parameters["nextRetry"] = item.Status == SendStatus.ERROR ? item.NextRetry.ToString("O") : null;
        _hooks.Dispatch(HookEvent.OnSendFailed, parameters);
        DispatchItemWebhook(item, "file.send_failed");
    }

    /// <summary>Content of an outgoing file after signing, compression and encryption were applied to it.</summary>
    private sealed record SecuredContent(
        FileSecuritySettings Settings, long OriginalSize, Func<CancellationToken, ValueTask<Stream>> OpenAsync);

    /// <summary>
    /// Applies the file level security configured for the partner to the content of <paramref name="item"/> and
    /// remembers the hash sent back in the End to End Response. Secured content is prepared in memory, so files
    /// above the configured limit are refused here instead of being sent unprotected.
    /// </summary>
    private async Task<SecuredContent> SecureAsync(SendQueueItem item, CancellationToken cancellationToken)
    {
        var partner = Partner!;
        var settings = await _fileSecurity.ForSendingAsync(partner, cancellationToken);
        var originalSize = new FileInfo(item.FilePath).Length;

        item.SignedResponseRequested = partner.RequestSignedEndResponse;
        item.CipherSuite = settings.Any ? settings.Suite.Code : null;
        item.ContentHash = null;

        // The content is converted to the encoding configured for the partner before it is secured.
        Stream OpenContent() => PartnerEncoding.ForSending(partner,
            new FileStream(item.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true));

        if (!settings.Any)
        {
            // Nothing is applied to the content; the hash is still needed when we ask for a signed response.
            if (item.SignedResponseRequested)
            {
                await using var content = OpenContent();
                item.ContentHash = await settings.Suite.ComputeHashAsync(content, cancellationToken);
            }

            return new SecuredContent(settings, originalSize, _ => ValueTask.FromResult(OpenContent()));
        }

        if (originalSize > _fileSecurity.MaxSecuredFileSize)
            throw new FileSecurityException(
                $"The file has {originalSize / 1024 / 1024} MB, files secured for a partner are processed in memory " +
                $"and must not be larger than {_fileSecurity.MaxSecuredFileSizeText} (setting MaxSecuredFileSizeMb).");

        byte[] plain;
        await using (var content = OpenContent())
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            plain = buffer.ToArray();
        }

        var secured = FileSecurity.Protect(plain, settings);
        item.ContentHash = FileSecurity.ComputeHash(secured, settings.Suite);

        _logger.LogInformation("File {VirtualFileName} for {Partner} secured ({Applied}): {Original} B -> {Secured} B",
            item.VirtualFileName, partner.Name, Applied(settings), plain.Length, secured.Length);

        return new SecuredContent(settings, originalSize,
            _ => ValueTask.FromResult<Stream>(new MemoryStream(secured, writable: false)));
    }

    /// <summary>The content of the file was signed, compressed or encrypted, so it cannot be continued.</summary>
    private static bool IsSecured(OftpOutgoingFile file) =>
        file.SecurityLevel != SecurityLevels.None ||
        file.Compression != FileCompressionAlgorithms.None ||
        file.Enveloping != FileEnvelopingFormats.None;

    private static string Applied(FileSecuritySettings settings) => string.Join(", ",
        new[] { settings.Sign ? "signed" : null, settings.Compress ? "compressed" : null, settings.Encrypt ? "encrypted" : null }
            .Where(a => a is not null));

    #endregion

    #region Receiving

    public override async ValueTask<OftpStartFileDecision> OnStartFileAsync(SFID header, CancellationToken cancellationToken)
    {
        var partner = Partner!;

        if (await _identities.FindBySfidAsync(header.Destination, cancellationToken) is null)
            return OftpStartFileDecision.Reject(AnswerReasonCodes.InvalidDestination, $"Unknown destination {header.Destination}.");

        if (!SameCode(header.Originator, partner.SFID))
            _logger.LogInformation("File {VirtualFileName} from {Partner} was created by {Originator}",
                header.DatasetName, partner.Name, header.Originator);

        if (await _receivedFiles.ExistsAsync(partner.Id, header.DatasetName, header.Date, header.Time, cancellationToken))
            return OftpStartFileDecision.Reject(AnswerReasonCodes.DuplicateFile, "File was already received.");

        var security = FileSecurityDescriptor.From(header);
        if (await CheckIncomingSecurityAsync(header, security, cancellationToken) is { } refusal)
            return refusal;

        var directory = Path.Combine(_settingsService.ResolvePath(_settings.ReceiveDirectory), SafeFileName(partner.SSID));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{SafeFileName(header.DatasetName)}_{header.Date}{header.Time}");

        // A file we already have a part of is either continued or received again, but always under the same record.
        // Secured content is sent anew every time, so only a file stored as it arrives can be continued.
        var interrupted = await _receivedFiles.FindInterruptedAsync(partner.Id, header.DatasetName, header.Date, header.Time,
            cancellationToken);
        var canContinue = interrupted is not null && partner.Restart && !security.Any && header.RestartPosition > 0;

        var record = interrupted ?? new ReceivedFile { Created = _timeService.GetCurrentTime(), FilePath = path };
        record.PartnerId = partner.Id;
        record.VirtualFileName = header.DatasetName;
        record.FileDate = header.Date;
        record.FileTime = header.Time;
        record.UserData = header.UserData;
        record.Originator = header.Originator;
        record.Destination = header.Destination;
        record.Description = header.Description;
        record.SecurityLevel = header.SecurityLevel;
        record.CipherSuite = security.Any || header.SignedEerpRequested ? header.CipherSuite : null;
        record.Compressed = security.Compressed;
        record.SignedResponseRequested = header.SignedEerpRequested;
        record.Status = ReceiveStatus.RECEIVING;
        record.LastError = null;

        Stream stream;
        long restartPosition;
        try
        {
            restartPosition = canContinue ? KeepCompleteBlocks(record.FilePath + ".part", header.RestartPosition) : 0;

            var file = new FileStream(record.FilePath + ".part",
                restartPosition > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            // Secured content is stored as received and unpacked (and converted) when the transfer is complete;
            // plain content is converted from EBCDIC to ANSI while it is written when the partner is configured so.
            stream = security.Any ? file : PartnerEncoding.ForReceiving(partner, file);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Cannot store received file {Path}", path);
            return OftpStartFileDecision.Reject(AnswerReasonCodes.AccessMethodFailure, "Cannot store the file.", retryLater: true);
        }

        if (interrupted is null)
            _unitOfWork.AddForInsert(record);
        else
            _unitOfWork.AddForUpdate(record);
        await _unitOfWork.CommitAsync(cancellationToken);

        if (restartPosition > 0)
            _logger.LogInformation("Receiving {VirtualFileName} from {Partner} continues at block {Block}",
                record.VirtualFileName, partner.Name, restartPosition);

        _fileStopwatch.Restart();
        return OftpStartFileDecision.Accept(stream, record, restartPosition);
    }

    /// <summary>
    /// Trims a partially received file to complete 1K blocks, at most to the position offered by the partner,
    /// and returns the number of blocks that are kept. An incomplete last block is dropped, because the partner
    /// counts in whole blocks.
    /// </summary>
    private static long KeepCompleteBlocks(string path, long offeredBlocks)
    {
        if (!File.Exists(path))
            return 0;

        var blocks = Math.Min(offeredBlocks, new FileInfo(path).Length / OftpSession.RestartBlockSize);
        if (blocks <= 0)
        {
            File.Delete(path);
            return 0;
        }

        using var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        file.SetLength(blocks * OftpSession.RestartBlockSize);
        return blocks;
    }

    public override ValueTask OnStartFileRefusedAsync(SFID header, OftpAnswer answer, CancellationToken cancellationToken)
    {
        Record(TransferEventCategory.Incoming, TransferEventType.FileRefused, TransferEventLevel.Warning,
            $"{header.DatasetName} from {header.Originator} refused ({answer.ReasonCode}): {answer.ReasonText}",
            e =>
            {
                e.VirtualFileName = header.DatasetName;
                e.FileDate = header.Date;
                e.FileTime = header.Time;
            });
        return ValueTask.CompletedTask;
    }

    public override async ValueTask<OftpAnswer> OnFileReceivedAsync(OftpIncomingFile file, CancellationToken cancellationToken)
    {
        var record = (ReceivedFile)file.State!;
        try
        {
            await StoreContentAsync(file.Header, record, cancellationToken);
        }
        catch (FileSecurityException ex)
        {
            _logger.LogError(ex, "Unpacking {VirtualFileName} from {Partner} failed", record.VirtualFileName, Partner?.Name);
            await MarkReceiveFailedAsync(record, ex.Message);
            return OftpAnswer.Reject(ex.ReasonCode, ex.Message);
        }
        catch (IOException ex)
        {
            await MarkReceiveFailedAsync(record, ex.Message);
            return OftpAnswer.Reject(AnswerReasonCodes.AccessMethodFailure, "Cannot store the file.");
        }

        record.Size = new FileInfo(record.FilePath).Length;
        record.RestartedFrom = file.RestartPosition;
        record.Status = ReceiveStatus.RECEIVED;
        FilesReceived++;
        await SaveAsync(record);
        Record(TransferEventCategory.Incoming, TransferEventType.FileReceived, TransferEventLevel.Information,
            $"{record.VirtualFileName} received from {Partner?.Name}", e => Describe(e, record, _fileStopwatch.ElapsedMilliseconds));
        _hooks.Dispatch(HookEvent.OnReceived, ReceiveHookParameters(record));
        await DispatchInboxWebhooksAsync(record, cancellationToken);
        return OftpAnswer.Accept();
    }

    public override async ValueTask OnFileReceiveFailedAsync(OftpIncomingFile file, string reason, CancellationToken cancellationToken)
    {
        // What arrived is kept only when the partner can continue the transfer later.
        var keepPartial = Partner is { Restart: true } && file.TotalBytes >= OftpSession.RestartBlockSize &&
                          !FileSecurityDescriptor.From(file.Header).Any;

        await MarkReceiveFailedAsync((ReceivedFile)file.State!, reason, keepPartial, file.TotalBytes);
    }

    private async Task MarkReceiveFailedAsync(ReceivedFile record, string reason,
        bool keepPartial = false, long receivedBytes = 0)
    {
        if (!keepPartial)
        {
            try
            {
                File.Delete(record.FilePath + ".part");
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Cannot delete partial file {Path}", record.FilePath);
            }
        }
        else
        {
            record.Size = receivedBytes;
            _logger.LogInformation("{Bytes} octets of {VirtualFileName} are kept for a restart by {Partner}",
                receivedBytes, record.VirtualFileName, Partner?.Name);
        }

        record.Status = keepPartial ? ReceiveStatus.INTERRUPTED : ReceiveStatus.FAILED;
        record.LastError = reason.Length > 2000 ? reason[..2000] : reason;
        await SaveAsync(record);

        Record(TransferEventCategory.Incoming, TransferEventType.FileReceiveFailed,
            keepPartial ? TransferEventLevel.Warning : TransferEventLevel.Error,
            keepPartial
                ? $"Receiving {record.VirtualFileName} was interrupted after {receivedBytes} octets, which are kept for a restart: {reason}"
                : $"Receiving {record.VirtualFileName} failed: {reason}",
            e => Describe(e, record, null));

        var parameters = ReceiveHookParameters(record);
        parameters["error"] = reason;
        _hooks.Dispatch(HookEvent.OnReceiveFailed, parameters);
    }

    /// <summary>
    /// Refuses a file whose security we cannot handle before it is transferred, with the reason code that tells
    /// the partner what is wrong (RFC 5024, section 5.3.4).
    /// </summary>
    private async Task<OftpStartFileDecision?> CheckIncomingSecurityAsync(SFID header, FileSecurityDescriptor security,
        CancellationToken cancellationToken)
    {
        if (!security.Any && !header.SignedEerpRequested)
            return null;

        if (CipherSuite.Get(header.CipherSuite) is null && (security.Encrypted || security.Signed || header.SignedEerpRequested))
            return OftpStartFileDecision.Reject(AnswerReasonCodes.CipherSuiteNotSupported,
                $"Cipher suite '{header.CipherSuite}' is not supported.");

        if (security.Compressed && header.Compression != FileCompressionAlgorithms.Zlib)
            return OftpStartFileDecision.Reject(AnswerReasonCodes.CompressionNotAllowed,
                $"Compression algorithm '{header.Compression}' is not supported.");

        var ownCertificate = await _fileSecurity.GetOwnCertificateAsync(cancellationToken);

        if ((security.Encrypted || header.SignedEerpRequested) && ownCertificate is null)
            return OftpStartFileDecision.Reject(
                security.Encrypted ? AnswerReasonCodes.FileDecryptionFailure : AnswerReasonCodes.UnspecifiedReason,
                "No certificate for file security is configured (setting FileSecurityCertificateId).");

        if (security.Signed && await _fileSecurity.GetPartnerCertificateAsync(Partner!, cancellationToken) is null)
            return OftpStartFileDecision.Reject(AnswerReasonCodes.InvalidFileSignature,
                $"Partner {Partner!.Name} has no certificate configured to verify file signatures with.");

        // Unpacking is done in memory. SFIDFSIZ is in 1K blocks and only an estimate, the real size is checked again.
        if (security.Any && header.FileSize * 1024 > _fileSecurity.MaxSecuredFileSize)
            return OftpStartFileDecision.Reject(AnswerReasonCodes.FileSizeTooBig,
                $"Secured files must not be larger than {_fileSecurity.MaxSecuredFileSizeText} (setting MaxSecuredFileSizeMb).");

        return null;
    }

    /// <summary>
    /// Moves the received content to its final place: secured content is hashed for the End to End Response,
    /// then decrypted, decompressed, signature checked and converted to the local encoding.
    /// </summary>
    private async Task StoreContentAsync(SFID header, ReceivedFile record, CancellationToken cancellationToken)
    {
        var security = FileSecurityDescriptor.From(header);
        var partial = record.FilePath + ".part";

        if (!security.Any && !header.SignedEerpRequested)
        {
            File.Move(partial, record.FilePath, overwrite: true);
            return;
        }

        var transferred = new FileInfo(partial).Length;
        if (transferred > _fileSecurity.MaxSecuredFileSize)
            throw new FileSecurityException(AnswerReasonCodes.FileSizeTooBig,
                $"The file has {transferred / 1024 / 1024} MB, secured files are processed in memory and must not be " +
                $"larger than {_fileSecurity.MaxSecuredFileSizeText} (setting MaxSecuredFileSizeMb).");

        var content = await File.ReadAllBytesAsync(partial, cancellationToken);

        // The hash of the transferred (still secured) content is what the partner asked us to sign in the EERP.
        var suite = CipherSuite.Get(header.CipherSuite) ?? CipherSuite.Default;
        if (header.SignedEerpRequested)
            record.ContentHash = FileSecurity.ComputeHash(content, suite);

        if (!security.Any)
        {
            File.Move(partial, record.FilePath, overwrite: true);
            return;
        }

        var plain = FileSecurity.Unprotect(content, security,
            await _fileSecurity.GetOwnCertificateAsync(cancellationToken),
            await _fileSecurity.GetPartnerCertificateAsync(Partner!, cancellationToken));

        await using (var destination = PartnerEncoding.ForReceiving(Partner!,
                         new FileStream(record.FilePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true)))
        {
            await destination.WriteAsync(plain, cancellationToken);
        }

        File.Delete(partial);

        _logger.LogInformation("File {VirtualFileName} from {Partner} unpacked: {Transferred} B -> {Plain} B",
            record.VirtualFileName, Partner?.Name, content.Length, plain.Length);
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Trim().Select(c => invalid.Contains(c) || c is '/' or '\\' ? '_' : c).ToArray();
        return chars.Length == 0 ? "_" : new string(chars);
    }

    #endregion

    #region End to end responses

    public override async ValueTask<IReadOnlyList<OftpCommand>> GetPendingEndResponsesAsync(CancellationToken cancellationToken)
    {
        _pendingResponses.Clear();

        foreach (var record in await _receivedFiles.GetUnconfirmedAsync(Partner!.Id, cancellationToken))
        {
            var claim = TransferClaims.ForReceivedFile(record.Id);
            if (!_claims.TryClaim(claim))
                continue;
            _claimed.Add(claim);

            // A file that could not be delivered to its final destination is reported with a NERP instead.
            OftpCommand response = record.Status == ReceiveStatus.NOT_DELIVERED
                ? new NERP
                {
                    DatasetName = record.VirtualFileName,
                    Date = record.FileDate,
                    Time = record.FileTime,
                    Destination = record.Originator,
                    Originator = record.Destination,
                    // We are the location that could not deliver the file, so we are the creator of the response.
                    Creator = record.Destination,
                    ReasonCode = record.NotDeliveredReasonCode ?? AnswerReasonCodes.UnspecifiedReason,
                    ReasonText = Truncate(record.LastError, 999),
                    Hash = record.ContentHash ?? [],
                }
                : new EERP
                {
                    DatasetName = record.VirtualFileName,
                    Date = record.FileDate,
                    Time = record.FileTime,
                    UserData = record.UserData,
                    Destination = record.Originator,
                    Originator = record.Destination,
                    Hash = record.ContentHash ?? [],
                };

            _pendingResponses.Add(await SignResponseAsync(response, record, cancellationToken), record);
        }

        return _pendingResponses.Keys.ToList();
    }

    /// <summary>
    /// Signs the End to End Response when the partner asked for it (SFIDSIGN). A response that cannot be signed is
    /// still sent unsigned: the file was handled and the partner decides whether it accepts that.
    /// </summary>
    private async Task<OftpCommand> SignResponseAsync(OftpCommand response, ReceivedFile record, CancellationToken cancellationToken)
    {
        if (!record.SignedResponseRequested)
            return response;

        try
        {
            var certificate = await _fileSecurity.GetOwnCertificateAsync(cancellationToken)
                ?? throw new FileSecurityException("No certificate for file security is configured (setting FileSecurityCertificateId).");
            var suite = CipherSuite.Get(record.CipherSuite) ?? CipherSuite.Default;

            return response switch
            {
                EERP eerp => new EERP
                {
                    DatasetName = eerp.DatasetName,
                    Date = eerp.Date,
                    Time = eerp.Time,
                    UserData = eerp.UserData,
                    Destination = eerp.Destination,
                    Originator = eerp.Originator,
                    Hash = eerp.Hash,
                    Signature = FileSecurity.SignEndResponse(EndResponseSignature.GetSignedContent(eerp), certificate, suite),
                },
                NERP nerp => new NERP
                {
                    DatasetName = nerp.DatasetName,
                    Date = nerp.Date,
                    Time = nerp.Time,
                    Destination = nerp.Destination,
                    Originator = nerp.Originator,
                    Creator = nerp.Creator,
                    ReasonCode = nerp.ReasonCode,
                    ReasonText = nerp.ReasonText,
                    Hash = nerp.Hash,
                    Signature = FileSecurity.SignEndResponse(EndResponseSignature.GetSignedContent(nerp), certificate, suite),
                },
                _ => response,
            };
        }
        catch (FileSecurityException ex)
        {
            _logger.LogWarning(ex, "{Response} for {VirtualFileName} requested by {Partner} cannot be signed",
                response.Name, record.VirtualFileName, Partner?.Name);
            Record(TransferEventCategory.EndResponse, TransferEventType.EndResponseSignatureInvalid, TransferEventLevel.Warning,
                $"{response.Name} for {record.VirtualFileName} is sent unsigned although {Partner?.Name} asked for a signature: {ex.Message}",
                e => Describe(e, record, null));
            return response;
        }
    }

    private static string Truncate(string? value, int length)
    {
        var text = value ?? "";
        return text.Length <= length ? text : text[..length];
    }

    public override async ValueTask OnEndResponseSentAsync(OftpCommand response, CancellationToken cancellationToken)
    {
        if (!_pendingResponses.Remove(response, out var record))
            return;

        // A file reported as not delivered keeps that state, the response only records that the partner knows.
        var negative = response is NERP;
        if (!negative)
            record.Status = ReceiveStatus.CONFIRMED;

        record.ConfirmedDate = _timeService.GetCurrentTime();
        await SaveAsync(record);

        Record(TransferEventCategory.EndResponse,
            negative ? TransferEventType.NerpSent : TransferEventType.EerpSent,
            negative ? TransferEventLevel.Warning : TransferEventLevel.Information,
            negative
                ? $"NERP for {record.VirtualFileName} sent to {Partner?.Name} ({record.NotDeliveredReasonCode}): {record.LastError}"
                : $"EERP for {record.VirtualFileName} sent to {Partner?.Name}",
            e => Describe(e, record, null));
    }

    public override async ValueTask OnEndResponseReceivedAsync(OftpCommand response, CancellationToken cancellationToken)
    {
        var (datasetName, date, time) = response switch
        {
            EERP eerp => (eerp.DatasetName, eerp.Date, eerp.Time),
            NERP nerp => (nerp.DatasetName, nerp.Date, nerp.Time),
            _ => throw new ArgumentException("Unexpected end response.", nameof(response))
        };

        var item = await _sendQueue.FindSentAsync(datasetName, date, time, cancellationToken);
        if (item is null)
        {
            _logger.LogWarning("Received {Response} for unknown file {VirtualFileName} {Date} {Time}",
                response.Name, datasetName, date, time);
            Record(TransferEventCategory.EndResponse, TransferEventType.UnknownEndResponse, TransferEventLevel.Warning,
                $"{response.Name} for unknown file {datasetName} {date} {time} received from {Partner?.Name}",
                e =>
                {
                    e.VirtualFileName = datasetName;
                    e.FileDate = date;
                    e.FileTime = time;
                });
            return;
        }

        if (await VerifyResponseAsync(response, item, cancellationToken) is { } invalid)
        {
            // The file was answered, but the answer does not prove anything: keep it as sent and report the problem.
            item.LastError = invalid;
            item.LastErrorDate = _timeService.GetCurrentTime();
            await SaveAsync(item);
            _logger.LogWarning("Signature of the {Response} for {VirtualFileName} from {Partner} is not valid: {Error}",
                response.Name, item.VirtualFileName, Partner?.Name, invalid);
            Record(TransferEventCategory.EndResponse, TransferEventType.EndResponseSignatureInvalid, TransferEventLevel.Error,
                $"Signature of the {response.Name} for {item.VirtualFileName} from {Partner?.Name} is not valid: {invalid}",
                e => Describe(e, item, null));
            return;
        }

        if (response is NERP nerpResponse)
        {
            item.Status = SendStatus.NOT_DELIVERED;
            item.LastError = $"NERP from {nerpResponse.Creator} ({nerpResponse.ReasonCode}): {nerpResponse.ReasonText}";
            item.LastErrorDate = _timeService.GetCurrentTime();
            _logger.LogWarning("File {VirtualFileName} was not delivered: {Error}", datasetName, item.LastError);
        }
        else
        {
            item.Status = SendStatus.DELIVERED;
            item.DeliveredDate = _timeService.GetCurrentTime();
            _logger.LogInformation("File {VirtualFileName} delivered to {Partner}", datasetName, Partner?.Name);
        }

        await SaveAsync(item);

        var parameters = SendHookParameters(item);
        if (response is NERP negativeResponse)
        {
            parameters["reasonCode"] = negativeResponse.ReasonCode;
            parameters["reasonText"] = negativeResponse.ReasonText;
            parameters["creator"] = negativeResponse.Creator;
            Record(TransferEventCategory.EndResponse, TransferEventType.NerpReceived, TransferEventLevel.Warning,
                $"NERP for {item.VirtualFileName} received from {Partner?.Name}: {item.LastError}", e => Describe(e, item, null));
            _hooks.Dispatch(HookEvent.OnNotDelivered, parameters);
            DispatchItemWebhook(item, "file.not_delivered");
        }
        else
        {
            Record(TransferEventCategory.EndResponse, TransferEventType.EerpReceived, TransferEventLevel.Information,
                $"EERP for {item.VirtualFileName} received from {Partner?.Name}", e => Describe(e, item, null));
            _hooks.Dispatch(HookEvent.OnDelivered, parameters);
            DispatchItemWebhook(item, "file.delivered");
        }
    }

    /// <summary>
    /// Checks the signature of an End to End Response we asked to be signed: the signature has to be made by the
    /// partner's certificate and the hash has to be the one of the content we sent. Returns the problem, or
    /// <c>null</c> when the response is acceptable.
    /// </summary>
    private async Task<string?> VerifyResponseAsync(OftpCommand response, SendQueueItem item, CancellationToken cancellationToken)
    {
        if (!item.SignedResponseRequested)
            return null;

        var (hash, signature, signedContent) = response switch
        {
            EERP eerp => (eerp.Hash, eerp.Signature, EndResponseSignature.GetSignedContent(eerp)),
            NERP nerp => (nerp.Hash, nerp.Signature, EndResponseSignature.GetSignedContent(nerp)),
            _ => ([], [], [])
        };

        if (signature.Length == 0)
            return "the response is not signed although a signature was requested";

        if (item.ContentHash is { } expected && hash.Length > 0 && !hash.SequenceEqual(expected))
            return "the hash of the file in the response differs from the hash of the content we sent";

        try
        {
            var certificate = await _fileSecurity.GetPartnerCertificateAsync(Partner!, cancellationToken)
                ?? throw new FileSecurityException($"partner {Partner!.Name} has no certificate configured");
            FileSecurity.VerifyEndResponse(signature, signedContent, certificate);
            return null;
        }
        catch (FileSecurityException ex)
        {
            return ex.Message;
        }
    }

    #endregion

    #region Webhooks

    /// <summary>Notifies the webhook registered with the file through the REST API.</summary>
    private void DispatchItemWebhook(SendQueueItem item, string eventName)
    {
        if (string.IsNullOrEmpty(item.WebhookUrl))
            return;

        _webhooks.Dispatch(item.WebhookUrl, item.WebhookSecret, new WebhookPayload
        {
            Event = eventName,
            QueueItemId = item.Id,
            Reference = item.Reference,
            VirtualFileName = item.VirtualFileName,
            FileDate = item.FileDate,
            FileTime = item.FileTime,
            FileSize = File.Exists(item.FilePath) ? new FileInfo(item.FilePath).Length : null,
            PartnerName = Partner?.Name,
            PartnerSsid = Partner?.SSID,
            Originator = item.Identity?.SFID,
            Destination = Partner?.SFID,
            Status = item.Status.ToString(),
            Error = item.LastError,
            SentDate = item.SentDate,
            DeliveredDate = item.DeliveredDate,
        });
    }

    /// <summary>Notifies webhooks registered with API tokens about a received file.</summary>
    private async Task DispatchInboxWebhooksAsync(ReceivedFile record, CancellationToken cancellationToken)
    {
        foreach (var token in await _apiTokens.GetInboxWebhooksAsync(cancellationToken))
        {
            _webhooks.Dispatch(token.InboxWebhookUrl!, token.WebhookSecret, new WebhookPayload
            {
                Event = "file.received",
                ReceivedFileId = record.Id,
                VirtualFileName = record.VirtualFileName,
                FileDate = record.FileDate,
                FileTime = record.FileTime,
                FileSize = record.Size,
                PartnerName = Partner?.Name,
                PartnerSsid = Partner?.SSID,
                Originator = record.Originator,
                Destination = record.Destination,
                Status = record.Status.ToString(),
            });
        }
    }

    #endregion

    #region Transfer events

    /// <summary>Records the end of the session (called by the service running it).</summary>
    public void RecordSessionEnd(Exception? exception, TimeSpan duration)
    {
        if (exception is null)
        {
            Record(ConnectionCategory, TransferEventType.SessionEnded, TransferEventLevel.Information,
                $"Session with {Partner?.Name} ended: {FilesSent} file(s) sent, {FilesReceived} file(s) received",
                e => e.DurationMs = (long)duration.TotalMilliseconds);
        }
        else
        {
            Record(ConnectionCategory, TransferEventType.SessionFailed, TransferEventLevel.Error,
                $"Session with {Partner?.Name ?? _remoteEndPoint} failed: {exception.Message}",
                e =>
                {
                    e.DurationMs = (long)duration.TotalMilliseconds;
                    e.Details = exception.ToString();
                });
        }
    }

    private void Record(TransferEventCategory category, TransferEventType type, TransferEventLevel level, string message,
        Action<TransferEvent>? configure = null)
    {
        var transferEvent = new TransferEvent
        {
            Timestamp = _timeService.GetCurrentTime(),
            Category = category,
            Type = type,
            Level = level,
            Message = message,
            PartnerId = Partner?.Id,
            PartnerName = Partner?.Name,
            RemoteEndPoint = _remoteEndPoint,
        };
        configure?.Invoke(transferEvent);
        _events.Record(transferEvent);
    }

    private static void Describe(TransferEvent e, SendQueueItem item, long? durationMs)
    {
        e.SendQueueItemId = item.Id;
        e.VirtualFileName = item.VirtualFileName;
        e.FileDate = item.FileDate;
        e.FileTime = item.FileTime;
        e.FileSize = File.Exists(item.FilePath) ? new FileInfo(item.FilePath).Length : null;
        e.DurationMs = durationMs;
        e.Details = item.LastError;
    }

    private static void Describe(TransferEvent e, ReceivedFile record, long? durationMs)
    {
        e.ReceivedFileId = record.Id;
        e.VirtualFileName = record.VirtualFileName;
        e.FileDate = record.FileDate;
        e.FileTime = record.FileTime;
        e.FileSize = record.Size > 0 ? record.Size : null;
        e.DurationMs = durationMs;
        e.Details = record.LastError;
    }

    #endregion

    #region Hook parameters

    private Dictionary<string, string?> PartnerHookParameters() => new()
    {
        ["partnerName"] = Partner?.Name,
        ["partnerSsid"] = Partner?.SSID,
        ["partnerSfid"] = Partner?.SFID,
    };

    private Dictionary<string, string?> SendHookParameters(SendQueueItem item)
    {
        var parameters = PartnerHookParameters();
        parameters["queueItemId"] = item.Id.ToString();
        parameters["virtualFileName"] = item.VirtualFileName;
        parameters["fileDate"] = item.FileDate;
        parameters["fileTime"] = item.FileTime;
        parameters["filePath"] = item.FilePath;
        parameters["fileSize"] = File.Exists(item.FilePath) ? new FileInfo(item.FilePath).Length.ToString() : null;
        parameters["description"] = item.Description;
        parameters["identityName"] = item.Identity?.Name;
        parameters["originator"] = item.Identity?.SFID;
        parameters["destination"] = Partner?.SFID;
        parameters["status"] = item.Status.ToString();
        return parameters;
    }

    private Dictionary<string, string?> ReceiveHookParameters(ReceivedFile record)
    {
        var parameters = PartnerHookParameters();
        parameters["receivedFileId"] = record.Id.ToString();
        parameters["virtualFileName"] = record.VirtualFileName;
        parameters["fileDate"] = record.FileDate;
        parameters["fileTime"] = record.FileTime;
        parameters["filePath"] = record.FilePath;
        parameters["fileSize"] = record.Size.ToString();
        parameters["description"] = record.Description;
        parameters["userData"] = record.UserData;
        parameters["originator"] = record.Originator;
        parameters["destination"] = record.Destination;
        parameters["status"] = record.Status.ToString();
        return parameters;
    }

    #endregion

    private async Task SaveAsync<TEntity>(TEntity entity) where TEntity : class
    {
        _unitOfWork.AddForUpdate(entity);
        // Persist state changes even when the session is being cancelled.
        await _unitOfWork.CommitAsync(CancellationToken.None);
    }

    public void Dispose()
    {
        _fileSecurity.Dispose();

        foreach (var claim in _claimed)
            _claims.Release(claim);
        _claimed.Clear();
    }
}
