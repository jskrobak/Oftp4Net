using Havit.Data.Patterns.UnitOfWorks;
using Havit.Services.TimeServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Oftp4Net.Core.Protocol;
using Oftp4Net.Core.Protocol.Commands;
using Oftp4Net.Core.Session;
using Oftp4Net.DataLayer.Repositories;
using Oftp4Net.Domain;

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
    private readonly ILogger _logger;

    private readonly Identity? _identity;
    private readonly Listener? _listener;
    private readonly List<string> _claimed = [];
    private readonly Dictionary<OftpCommand, ReceivedFile> _pendingResponses = new(ReferenceEqualityComparer.Instance);
    private Queue<SendQueueItem>? _outgoing;
    private SendQueueItem? _inFlight;

    private PartnerSessionHandler(IServiceProvider scopedServices, GlobalSettings settings, ILogger logger,
        Partner? partner, Identity? identity, Listener? listener)
    {
        _sendQueue = scopedServices.GetRequiredService<ISendQueueItemRepository>();
        _receivedFiles = scopedServices.GetRequiredService<IReceivedFileRepository>();
        _partners = scopedServices.GetRequiredService<IPartnerRepository>();
        _identities = scopedServices.GetRequiredService<IIdentityRepository>();
        _unitOfWork = scopedServices.GetRequiredService<IUnitOfWork>();
        _claims = scopedServices.GetRequiredService<TransferClaims>();
        _timeService = scopedServices.GetRequiredService<ITimeService>();
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
        new(scopedServices, settings, logger, partner, identity, listener: null);

    /// <summary>Handler for a session a partner opened to <paramref name="listener"/>.</summary>
    public static PartnerSessionHandler ForResponder(IServiceProvider scopedServices, GlobalSettings settings, ILogger logger,
        Listener listener) =>
        new(scopedServices, settings, logger, partner: null, identity: null, listener);

    /// <summary>The partner of the session; for a responder known after authentication.</summary>
    public Partner? Partner { get; private set; }

    public int FilesSent { get; private set; }

    /// <summary>At least one queued file was offered to the peer (SFID sent) in this session.</summary>
    public bool FileTransferStarted { get; private set; }
    public int FilesReceived { get; private set; }

    #region Authentication

    public override async ValueTask<OftpAuthenticationResult> AuthenticateAsync(SSID remote, CancellationToken cancellationToken)
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
    }

    public override async ValueTask OnFileRefusedAsync(OftpOutgoingFile file, OftpAnswer answer, CancellationToken cancellationToken)
    {
        var item = (SendQueueItem)file.State!;
        _inFlight = null;
        await MarkFailedAsync(item, $"Refused by partner ({answer.ReasonCode}): {answer.ReasonText}", answer.RetryLater);
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

    private async Task MarkFailedAsync(SendQueueItem item, string error, bool retry)
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

        return OftpStartFileDecision.Accept(stream, record);
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
