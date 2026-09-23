using Havit.Data.Patterns.UnitOfWorks;
using Havit.Services.TimeServices;
using Microsoft.Extensions.Logging;
using Oftp4Net.Core.Protocol;
using Oftp4Net.DataLayer.Repositories;
using Oftp4Net.Domain;
using Oftp4Net.Services.TransferEvents;

namespace Oftp4Net.Services.Pdx;

/// <summary>
/// OFTP2 Communication Setups (PDX) received from partners over OFTP (Odette OP08 part 3, "Handling a received
/// datasheet"): the datasheet is checked before the End to End Response, an incompatible one is answered with a NERP,
/// a compatible one is applied after the EERP has been sent (the EERP still goes with the old settings) or at its
/// <c>validfrom</c> time. Datasheets without a valid signature wait for the administrator, depending on
/// <see cref="GlobalSettings.PdxAutoApply"/>.
/// </summary>
public class PartnerSetupService(
    PdxImporter importer,
    IPartnerSetupDocumentRepository documents,
    IPartnerRepository partners,
    IReceivedFileRepository receivedFiles,
    IUnitOfWork unitOfWork,
    GlobalSettingsService settingsService,
    ITimeService timeService,
    ITransferEventLog events,
    PartnerSetupScheduler scheduler,
    ILogger<PartnerSetupService> logger)
{
    /// <summary>Virtual file name of a datasheet sent over OFTP.</summary>
    public const string VirtualFileName = "OFTP_COMMUNICATION_SETUP";

    /// <summary>NERP texts are limited (NERPREASL has three digits).</summary>
    private const int ReasonTextLength = 999;

    public static bool IsSetupFile(string virtualFileName) =>
        string.Equals(virtualFileName.Trim(), VirtualFileName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Evaluates a datasheet <paramref name="partner"/> sent in <paramref name="record"/> and sets the state of the
    /// file: RECEIVED (an EERP follows and the datasheet is applied after it), HELD (waits for approval) or
    /// NOT_DELIVERED (a NERP with the reason follows). The caller commits the changes with the file.
    /// </summary>
    public async Task ReceiveAsync(Partner partner, ReceivedFile record, byte[] content, PartnerSetupSignature signature,
        CancellationToken cancellationToken)
    {
        var plan = await importer.PlanAsync(content, cancellationToken, sender: partner);
        var settings = await settingsService.GetGlobalSettingsAsync();

        var document = PartnerSetupDocuments.Create(plan, PartnerSetupSource.Oftp, signature);
        document.Created = timeService.GetCurrentTime();
        document.Partner = partner;
        document.ReceivedFile = record;
        unitOfWork.AddForInsert(document);

        if (!plan.CanApply)
        {
            document.Status = PartnerSetupStatus.Rejected;
            document.DecidedDate = timeService.GetCurrentTime();
            Reject(record, "The OFTP2 Communication Setup is not accepted: " + string.Join(" ", plan.Errors));
            Record(partner, record, TransferEventType.SetupRejected, TransferEventLevel.Warning,
                $"OFTP2 Communication Setup from {partner.Name} rejected: {string.Join(" ", plan.Errors)}");
            return;
        }

        // A datasheet that came over ODETTE-FTP 1.x (no signatures there) always waits for the administrator (OP08).
        var automatic = ProtocolLevels.HasOftp2Features(partner.ProtocolLevel) && settings.PdxAutoApply switch
        {
            PdxAutoApply.Always => true,
            PdxAutoApply.SignedOnly => signature == PartnerSetupSignature.Valid,
            _ => false,
        };

        if (automatic)
        {
            document.Status = PartnerSetupStatus.AwaitingEndResponse;
            document.DecidedDate = timeService.GetCurrentTime();
            record.Status = ReceiveStatus.RECEIVED;
            Record(partner, record, TransferEventType.SetupReceived, TransferEventLevel.Information,
                $"OFTP2 Communication Setup from {partner.Name} accepted, it is applied after the EERP " +
                $"({plan.Changes.Count} change(s))");
        }
        else
        {
            document.Status = PartnerSetupStatus.PendingApproval;
            record.Status = ReceiveStatus.HELD;
            Record(partner, record, TransferEventType.SetupReceived, TransferEventLevel.Warning,
                $"OFTP2 Communication Setup from {partner.Name} waits for approval ({SignatureText(signature)})");
        }
    }

    /// <summary>
    /// Called when the End to End Response of <paramref name="record"/> was sent: an accepted datasheet in it is
    /// applied now, or scheduled for its <c>validfrom</c> time.
    /// </summary>
    public async Task OnEndResponseSentAsync(ReceivedFile record, CancellationToken cancellationToken)
    {
        var document = await documents.FindByReceivedFileAsync(record.Id, cancellationToken);
        if (document is not { Status: PartnerSetupStatus.AwaitingEndResponse })
            return;

        if (document.ValidFrom > timeService.GetCurrentTime())
        {
            document.Status = PartnerSetupStatus.Scheduled;
            unitOfWork.AddForUpdate(document);
            await unitOfWork.CommitAsync(CancellationToken.None);
            scheduler.Trigger();
            logger.LogInformation("OFTP2 Communication Setup {DocId} of {Ssid} is applied at {ValidFrom}",
                document.DocId, document.Ssid, document.ValidFrom);
            return;
        }

        await ApplyAsync(document, cancellationToken);
    }

    /// <summary>
    /// Keeps an uploaded datasheet of an existing partner until its <c>validfrom</c> time instead of applying it now.
    /// </summary>
    public async Task ScheduleUploadAsync(PdxImportPlan plan, string? user, CancellationToken cancellationToken = default)
    {
        if (!plan.CanApply || plan.Existing is null || plan.Document is null)
            throw new InvalidOperationException("Only a valid datasheet of an existing partner can be scheduled.");

        var document = PartnerSetupDocuments.Create(plan, PartnerSetupSource.Upload, PartnerSetupSignature.None);
        document.Created = timeService.GetCurrentTime();
        document.Partner = plan.Existing;
        document.Status = PartnerSetupStatus.Scheduled;
        document.DecidedDate = timeService.GetCurrentTime();
        document.DecidedBy = user;
        unitOfWork.AddForInsert(document);
        await unitOfWork.CommitAsync(cancellationToken);
        scheduler.Trigger();

        logger.LogInformation("OFTP2 Communication Setup {DocId} of {Ssid} uploaded by {User} is applied at {ValidFrom}",
            document.DocId, document.Ssid, user, document.ValidFrom);
    }

    /// <summary>Applies a scheduled datasheet before its time.</summary>
    public async Task ApplyNowAsync(int documentId, string? user, CancellationToken cancellationToken = default)
    {
        var document = await documents.GetObjectAsync(documentId, cancellationToken);
        if (document.Status != PartnerSetupStatus.Scheduled)
            throw new InvalidOperationException($"The datasheet is {document.Status}, only a scheduled one can be applied now.");

        document.DecidedBy = user;
        await ApplyAsync(document, cancellationToken);
    }

    /// <summary>Applies the scheduled datasheets whose time has come.</summary>
    public async Task ActivateDueAsync(CancellationToken cancellationToken)
    {
        foreach (var document in await documents.GetDueScheduledAsync(timeService.GetCurrentTime(), cancellationToken))
            await ApplyAsync(document, cancellationToken);
    }

    /// <summary>
    /// Accepts a datasheet waiting for approval: the EERP is sent in the next session with the partner and the
    /// datasheet is applied after it.
    /// </summary>
    public async Task ApproveAsync(int documentId, string? user, CancellationToken cancellationToken = default)
    {
        var document = await GetPendingAsync(documentId, cancellationToken);
        document.Status = PartnerSetupStatus.AwaitingEndResponse;
        document.DecidedDate = timeService.GetCurrentTime();
        document.DecidedBy = user;
        unitOfWork.AddForUpdate(document);

        if (document.ReceivedFileId is { } fileId && await receivedFiles.GetObjectAsync(fileId, cancellationToken) is { Status: ReceiveStatus.HELD } record)
        {
            record.Status = ReceiveStatus.RECEIVED;
            record.DecidedDate = timeService.GetCurrentTime();
            unitOfWork.AddForUpdate(record);
        }

        await unitOfWork.CommitAsync(cancellationToken);
        logger.LogInformation("OFTP2 Communication Setup {DocId} of {Ssid} approved by {User}", document.DocId, document.Ssid, user);
    }

    /// <summary>Refuses a datasheet waiting for approval: the partner gets a NERP with <paramref name="reason"/>.</summary>
    public async Task RejectAsync(int documentId, string? user, string reason, CancellationToken cancellationToken = default)
    {
        var document = await GetPendingAsync(documentId, cancellationToken);
        document.Status = PartnerSetupStatus.Rejected;
        document.DecidedDate = timeService.GetCurrentTime();
        document.DecidedBy = user;
        unitOfWork.AddForUpdate(document);

        var text = string.IsNullOrWhiteSpace(reason) ? "The OFTP2 Communication Setup was refused by the administrator." : reason.Trim();
        if (document.ReceivedFileId is { } fileId && await receivedFiles.GetObjectAsync(fileId, cancellationToken) is { Status: ReceiveStatus.HELD } record)
        {
            Reject(record, text);
            record.DecidedDate = timeService.GetCurrentTime();
            unitOfWork.AddForUpdate(record);
        }

        await unitOfWork.CommitAsync(cancellationToken);
        logger.LogInformation("OFTP2 Communication Setup {DocId} of {Ssid} rejected by {User}: {Reason}", document.DocId, document.Ssid, user, text);
    }

    private async Task<PartnerSetupDocument> GetPendingAsync(int documentId, CancellationToken cancellationToken)
    {
        var document = await documents.GetObjectAsync(documentId, cancellationToken);
        if (document.Status != PartnerSetupStatus.PendingApproval)
            throw new InvalidOperationException($"The datasheet is {document.Status}, only one waiting for approval can be decided.");
        return document;
    }

    /// <summary>
    /// Applies the datasheet to its partner. The partner may have changed since the datasheet was accepted, so it is
    /// compared again; when it cannot be applied any more it is marked as failed.
    /// </summary>
    private async Task ApplyAsync(PartnerSetupDocument document, CancellationToken cancellationToken)
    {
        var partner = document.PartnerId is { } partnerId ? await partners.GetObjectAsync(partnerId, cancellationToken) : null;
        // With its certificates, so that unchanged ones are recognised.
        if (partner is not null)
            partner = await partners.FindBySsidAsync(partner.SSID, cancellationToken) ?? partner;
        if (partner is null)
        {
            Fail(document, "The partner of the datasheet no longer exists.");
            await unitOfWork.CommitAsync(CancellationToken.None);
            return;
        }

        var plan = await importer.PlanAsync(PartnerSetupDocuments.Bytes(document), cancellationToken, sender: partner);
        if (!plan.CanApply)
        {
            Fail(document, string.Join(" ", plan.Errors));
            Record(partner, null, TransferEventType.SetupRejected, TransferEventLevel.Error,
                $"OFTP2 Communication Setup of {partner.Name} cannot be applied: {string.Join(" ", plan.Errors)}");
            await unitOfWork.CommitAsync(CancellationToken.None);
            return;
        }

        await importer.ApplyWithoutCommitAsync(plan, cancellationToken);

        document.Status = PartnerSetupStatus.Applied;
        document.AppliedDate = timeService.GetCurrentTime();
        document.Changes = PartnerSetupDocuments.Changes(plan);
        unitOfWork.AddForUpdate(document);
        await unitOfWork.CommitAsync(CancellationToken.None);

        Record(partner, null, TransferEventType.SetupApplied, TransferEventLevel.Information,
            $"OFTP2 Communication Setup of {partner.Name} applied ({plan.Changes.Count} change(s))");
    }

    private void Fail(PartnerSetupDocument document, string reason)
    {
        document.Status = PartnerSetupStatus.Failed;
        document.Messages = string.Join(Environment.NewLine, new[] { document.Messages, "Error: " + reason }.Where(m => !string.IsNullOrEmpty(m)));
        if (document.Messages.Length > 4000)
            document.Messages = document.Messages[..4000];
        unitOfWork.AddForUpdate(document);
        logger.LogError("OFTP2 Communication Setup {DocId} of {Ssid} cannot be applied: {Reason}", document.DocId, document.Ssid, reason);
    }

    private static void Reject(ReceivedFile record, string text)
    {
        record.Status = ReceiveStatus.NOT_DELIVERED;
        record.NotDeliveredReasonCode = AnswerReasonCodes.UnspecifiedReason;
        record.LastError = text.Length > ReasonTextLength ? text[..(ReasonTextLength - 1)] + "…" : text;
    }

    private static string SignatureText(PartnerSetupSignature signature) => signature switch
    {
        PartnerSetupSignature.Valid => "signed",
        PartnerSetupSignature.Invalid => "the signature could not be verified",
        _ => "not signed",
    };

    private void Record(Partner partner, ReceivedFile? record, TransferEventType type, TransferEventLevel level, string message) =>
        events.Record(new TransferEvent
        {
            Timestamp = timeService.GetCurrentTime(),
            Category = TransferEventCategory.Incoming,
            Type = type,
            Level = level,
            Message = message,
            PartnerId = partner.Id,
            PartnerName = partner.Name,
            ReceivedFileId = record?.Id,
            VirtualFileName = record?.VirtualFileName ?? VirtualFileName,
            FileDate = record?.FileDate,
            FileTime = record?.FileTime,
        });
}
