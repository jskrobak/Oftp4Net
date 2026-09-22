using Havit.Data.Patterns.UnitOfWorks;
using Havit.Services.TimeServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Oftp4Net.Core.Protocol;
using Oftp4Net.Core.Protocol.Commands;
using Oftp4Net.Core.Session;
using Oftp4Net.DataLayer.Repositories;
using Oftp4Net.Domain;
using System.Diagnostics;
using Oftp4Net.Services.Hooks;
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
    private readonly string _remoteEndPoint;
    private readonly Stopwatch _fileStopwatch = new();
    private readonly ILogger _logger;

    private readonly Identity? _identity;
    private readonly Listener? _listener;
    private readonly List<string> _claimed = [];
    private readonly Dictionary<OftpCommand, ReceivedFile> _pendingResponses = new(ReferenceEqualityComparer.Instance);
    private Queue<SendQueueItem>? _outgoing;
    private SendQueueItem? _inFlight;

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
        return OftpAuthenticationResult.Accept(_listener.Identity.SSID, _listener.Identity.Password ?? "");
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

            var (date, time) = OftpOutgoingFile.CreateTimestamp(_timeService.GetCurrentTime());
            item.FileDate = date;
            item.FileTime = time;
            _inFlight = item;
            FileTransferStarted = true;
            _fileStopwatch.Restart();

            return new OftpOutgoingFile
            {
                DatasetName = item.VirtualFileName,
                Originator = item.Identity.SFID,
                Destination = Partner!.SFID,
                Date = date,
                Time = time,
                Description = item.Description ?? "",
                State = item,
                OpenAsync = _ => ValueTask.FromResult<Stream>(new FileStream(item.FilePath, FileMode.Open,
                    FileAccess.Read, FileShare.Read, 81920, useAsync: true)),
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
        _inFlight = null;
        FilesSent++;
        await SaveAsync(item);
        Record(TransferEventCategory.Outgoing, TransferEventType.FileSent, TransferEventLevel.Information,
            $"{item.VirtualFileName} sent to {Partner?.Name}", e => Describe(e, item, _fileStopwatch.ElapsedMilliseconds));
        _hooks.Dispatch(HookEvent.OnSent, SendHookParameters(item));
    }

    public override async ValueTask OnFileRefusedAsync(OftpOutgoingFile file, OftpAnswer answer, CancellationToken cancellationToken)
    {
        var item = (SendQueueItem)file.State!;
        _inFlight = null;
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
            await MarkFailedAsync(_inFlight, exception.Message, retry: true);
            _inFlight = null;
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
    }

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

        var directory = Path.Combine(_settingsService.ResolvePath(_settings.ReceiveDirectory), SafeFileName(partner.SSID));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{SafeFileName(header.DatasetName)}_{header.Date}{header.Time}");

        var record = new ReceivedFile
        {
            Created = _timeService.GetCurrentTime(),
            PartnerId = partner.Id,
            VirtualFileName = header.DatasetName,
            FileDate = header.Date,
            FileTime = header.Time,
            UserData = header.UserData,
            Originator = header.Originator,
            Destination = header.Destination,
            Description = header.Description,
            FilePath = path,
            Status = ReceiveStatus.RECEIVING,
        };

        FileStream stream;
        try
        {
            stream = new FileStream(path + ".part", FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Cannot store received file {Path}", path);
            return OftpStartFileDecision.Reject(AnswerReasonCodes.AccessMethodFailure, "Cannot store the file.", retryLater: true);
        }

        _unitOfWork.AddForInsert(record);
        await _unitOfWork.CommitAsync(cancellationToken);

        _fileStopwatch.Restart();
        return OftpStartFileDecision.Accept(stream, record);
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
            File.Move(record.FilePath + ".part", record.FilePath, overwrite: true);
        }
        catch (IOException ex)
        {
            await MarkReceiveFailedAsync(record, ex.Message);
            return OftpAnswer.Reject(AnswerReasonCodes.AccessMethodFailure, "Cannot store the file.");
        }

        record.Size = file.BytesReceived;
        record.Status = ReceiveStatus.RECEIVED;
        FilesReceived++;
        await SaveAsync(record);
        Record(TransferEventCategory.Incoming, TransferEventType.FileReceived, TransferEventLevel.Information,
            $"{record.VirtualFileName} received from {Partner?.Name}", e => Describe(e, record, _fileStopwatch.ElapsedMilliseconds));
        _hooks.Dispatch(HookEvent.OnReceived, ReceiveHookParameters(record));
        return OftpAnswer.Accept();
    }

    public override async ValueTask OnFileReceiveFailedAsync(OftpIncomingFile file, string reason, CancellationToken cancellationToken)
    {
        await MarkReceiveFailedAsync((ReceivedFile)file.State!, reason);
    }

    private async Task MarkReceiveFailedAsync(ReceivedFile record, string reason)
    {
        try
        {
            File.Delete(record.FilePath + ".part");
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Cannot delete partial file {Path}", record.FilePath);
        }

        record.Status = ReceiveStatus.FAILED;
        record.LastError = reason.Length > 2000 ? reason[..2000] : reason;
        await SaveAsync(record);

        Record(TransferEventCategory.Incoming, TransferEventType.FileReceiveFailed, TransferEventLevel.Error,
            $"Receiving {record.VirtualFileName} failed: {reason}", e => Describe(e, record, null));

        var parameters = ReceiveHookParameters(record);
        parameters["error"] = reason;
        _hooks.Dispatch(HookEvent.OnReceiveFailed, parameters);
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

            _pendingResponses.Add(new EERP
            {
                DatasetName = record.VirtualFileName,
                Date = record.FileDate,
                Time = record.FileTime,
                UserData = record.UserData,
                Destination = record.Originator,
                Originator = record.Destination,
            }, record);
        }

        return _pendingResponses.Keys.ToList();
    }

    public override async ValueTask OnEndResponseSentAsync(OftpCommand response, CancellationToken cancellationToken)
    {
        if (!_pendingResponses.Remove(response, out var record))
            return;

        record.Status = ReceiveStatus.CONFIRMED;
        record.ConfirmedDate = _timeService.GetCurrentTime();
        await SaveAsync(record);
        Record(TransferEventCategory.EndResponse, TransferEventType.EerpSent, TransferEventLevel.Information,
            $"EERP for {record.VirtualFileName} sent to {Partner?.Name}", e => Describe(e, record, null));
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
        }
        else
        {
            Record(TransferEventCategory.EndResponse, TransferEventType.EerpReceived, TransferEventLevel.Information,
                $"EERP for {item.VirtualFileName} received from {Partner?.Name}", e => Describe(e, item, null));
            _hooks.Dispatch(HookEvent.OnDelivered, parameters);
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
        foreach (var claim in _claimed)
            _claims.Release(claim);
        _claimed.Clear();
    }
}
